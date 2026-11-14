using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>defrag.exe 输出解析（纯逻辑可测）：从 /A /V 分析输出中提取碎片率百分比。</summary>
public static class DefragOutputParser
{
    /// <summary>解析碎片率；中英文输出都认（「碎片: 12%」/「fragments: 12%」/「Free space fragmentation: 8%」）；读不到返回 null（不估算）。</summary>
    public static double? ParseFragmentationPercent(string output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        foreach (var line in output.Split('\n'))
        {
            // 优先匹配带「碎片/fragmentation」字样的行，取行内最后一个百分比
            if (line.Contains("碎片", StringComparison.OrdinalIgnoreCase)
                || line.Contains("fragmentation", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Matches(line, @"(\d+(?:\.\d+)?)\s*%");
                if (m.Count > 0 && double.TryParse(m[^1].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return Math.Clamp(v, 0, 100);
            }
        }
        // 兜底：整段输出里找「总数碎片」百分比行（Windows 中文口径「文件碎片占总数的百分比」）
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("百分比", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Matches(line, @"(\d+(?:\.\d+)?)\s*%");
                if (m.Count > 0 && double.TryParse(m[^1].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return Math.Clamp(v, 0, 100);
            }
        }
        return null;
    }
}

/// <summary>
/// 官方碎片整理执行器（R-P5-5）：全部经 Windows 自带 defrag.exe（官方碎片整理接口），
/// 只做整理与分析读数，绝不做任何非常规磁盘写入。非 Windows 平台如实返回未执行。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DefragExeRunner : IDefragRunner
{
    public async Task<double?> GetFragmentationPercentAsync(string volume, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);
        var output = await RunDefragAsync($"{volume.TrimEnd('\\')} /A /V", cancellation).ConfigureAwait(false);
        return DefragOutputParser.ParseFragmentationPercent(output);
    }

    public async Task<DefragOutcome> DefragAsync(string volume, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);
        progress?.Report($"开始整理 {volume}（Windows 官方碎片整理接口，进度可退后台，完成后本地通知）…");
        var output = await RunDefragAsync($"{volume.TrimEnd('\\')} /U /V", cancellation).ConfigureAwait(false);
        progress?.Report("整理命令已完成。");
        // 官方接口整轮执行成功即视为成功；碎片率前后读数由上层 DefragPlanner 复读补齐
        return new DefragOutcome(true, null, null, "defrag.exe 官方接口执行完毕（不做任何非常规磁盘写入）。");
    }

    private static async Task<string> RunDefragAsync(string arguments, CancellationToken cancellation)
    {
        var psi = new ProcessStartInfo("defrag.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("defrag.exe 启动失败");
        var output = await process.StandardOutput.ReadToEndAsync(cancellation).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"defrag.exe 退出码 {process.ExitCode}（可能需要管理员权限）");
        return output;
    }
}

/// <summary>
/// 磁盘介质类型探测（G8/R-P5-5 真机口径）：经 PowerShell Get-PhysicalDisk 读 MediaType
/// （HDD=机械盘 / SSD）。探测失败或非 Windows 返回 null（调用方按「类型未判定」如实降级，不假数）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsDiskTypeProbe
{
    /// <summary>探测指定卷所在物理盘是否机械盘；不可判定返回 null。</summary>
    public static bool? ProbeMechanical(string volume)
    {
        try
        {
            var letter = volume.TrimEnd('\\');
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -Command \"(Get-PhysicalDisk | Where-Object { $_.DeviceId -eq (Get-Partition -DriveLetter '\""
                + letter.Trim(':') + "\"').DiskNumber }).MediaType\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(15000);
            return output.ToUpperInvariant() switch
            {
                "HDD" => true,
                "SSD" => false,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SystemException)
        {
            return null; // 探测失败：类型未判定，调用方如实降级
        }
    }
}
