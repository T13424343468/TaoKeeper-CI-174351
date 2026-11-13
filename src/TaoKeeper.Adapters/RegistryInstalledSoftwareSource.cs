using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>
/// 一条注册表卸载登记（只读投影，纯逻辑可测）。
/// EstimatedSizeKb 取自注册表 EstimatedSize（单位 KB，部分安装器不写 → null 如实保留）。
/// </summary>
public sealed record RegistryUninstallRow(
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string? UninstallString,
    string? InstallLocation,
    long? EstimatedSizeKb,
    bool IsSystemComponent,
    string? RowKey);

/// <summary>注册表读取抽象：把「读哪个键、读什么值」与「怎么过滤、怎么去重」分开，沙箱可测。</summary>
public interface IUninstallRegistryReader
{
    /// <summary>枚举指定卸载键下的全部子键登记（subKeyPath 形如 HKLM\...\Uninstall\&lt;guid&gt;）。</summary>
    IReadOnlyList<RegistryUninstallRow> ReadUninstallKeys();
}

/// <summary>
/// 已装软件注册表真实枚举（R-P2-1：名称/版本/大小，只读，不设来源列）。
/// 扫三处标准卸载键：HKLM 64 位、HKLM WOW6432Node（32 位视图）、HKCU 当前用户；
/// 过滤系统组件与 Windows 系统补丁（用户不卸它们，混进清单只会添堵）；
/// 同名同版本登记（64/32 位双视图同一条软件）按 RowKey 去重。
/// 官方卸载入口=注册表 UninstallString（R-P2-5 卸载链的官方程序位）。
/// </summary>
public static class RegistryInstalledSoftwareSource
{
    /// <summary>标准卸载键路径（真机 Reader 按此枚举；测试假实现自行决定返回）。</summary>
    public static readonly string[] StandardUninstallKeys =
    {
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    /// <summary>过滤+去重成已装清单（纯逻辑，注入 reader 即测）。大小经 <see cref="SizeText"/> 展示。</summary>
    public static IReadOnlyList<SoftwareEntry> Collect(IUninstallRegistryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var rows = reader.ReadUninstallKeys();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SoftwareEntry>(rows.Count);
        foreach (var r in rows)
        {
            if (r.IsSystemComponent) continue;
            if (string.IsNullOrWhiteSpace(r.DisplayName)) continue;
            // Windows 更新/系统补丁类登记无卸载入口，卸载中心对用户无意义，如实过滤
            if (string.IsNullOrWhiteSpace(r.UninstallString) && LooksLikeSystemUpdate(r.DisplayName)) continue;

            // 双视图去重：64/32 位视图根路径不同（RowKey 必不同），登记同一软件的名称与版本一致，
            // 统一按「名称+版本」指纹去重；同名同版本不同软件极少且后果可接受（多显示一条）
            var key = $"{r.DisplayName}|{r.DisplayVersion ?? string.Empty}";
            if (!seen.Add(key)) continue;

            result.Add(new SoftwareEntry(
                Id: r.UninstallString ?? r.DisplayName,
                Name: r.DisplayName,
                Publisher: NullIfEmpty(r.Publisher),
                InstalledVersion: NullIfEmpty(r.DisplayVersion),
                AvailableVersion: null,
                SoftwareSourceKind.OfficialPageManual,
                HashVerified: null,
                SourceDisplay: null));
        }
        return result;
    }

    /// <summary>大小展示（R-P2-1 列口径）：KB → 人话 MB/GB；未知如实显「—」，不估不猜。</summary>
    public static string SizeText(long? estimatedSizeKb) => estimatedSizeKb switch
    {
        null => "—",
        < 1024 => $"{estimatedSizeKb} KB",
        < 1024 * 1024 => $"{Math.Round(estimatedSizeKb.Value / 1024d, 1):0.#} MB",
        _ => $"{Math.Round(estimatedSizeKb.Value / 1024d / 1024d, 2):0.##} GB",
    };

    /// <summary>Windows 自身更新登记的显示名特征（KB 补丁号等），无卸载入口时按系统项过滤。</summary>
    private static bool LooksLikeSystemUpdate(string displayName)
        => displayName.Contains("KB", StringComparison.OrdinalIgnoreCase)
           && displayName.Any(char.IsDigit);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
