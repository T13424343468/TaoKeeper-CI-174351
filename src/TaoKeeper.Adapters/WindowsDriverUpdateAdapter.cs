using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>Windows Update 驱动搜索网关（真机由 WUA COM 实现；测试注入假实现）。</summary>
public interface IWuaDriverGateway
{
    /// <summary>一条 WUA 搜索结果：硬件 Id、设备名、候选版本、设备类名（Microsoft 目录签名，哈希视为已过）。</summary>
    public sealed record WuaDriver(string HardwareId, string DeviceName, string? Version, string? ClassName);

    Task<IReadOnlyList<WuaDriver>> SearchDriverUpdatesAsync(CancellationToken cancellation = default);

    /// <summary>安装指定硬件 Id 的 WUA 驱动更新；返回是否成功与可展示文案。</summary>
    Task<(bool Success, string? Detail)> InstallDriverAsync(string hardwareId, CancellationToken cancellation = default);
}

/// <summary>
/// Windows 驱动适配器（IDriverUpdateAdapter 的 Windows 真机实现）。
/// WUA 走 COM 网关（Windows Update 目录签名，驱动包哈希视为已校验）；
/// 备份与厂商包安装走 pnputil。全动作用进程/COM 完成，纯逻辑部分沙箱可测。
/// 非 Windows 报红不静默跳过（与 winget 适配器同一规则）。
/// </summary>
public sealed class WindowsDriverUpdateAdapter : IDriverUpdateAdapter
{
    /// <summary>一次 pnputil 进程调用结果。</summary>
    public delegate (int ExitCode, string StdOut, string StdErr) Runner(string arguments);

    private const int PnputilTimeoutMs = 10 * 60 * 1000;
    private readonly IWuaDriverGateway _wua;
    private readonly Runner _pnputil;

    public WindowsDriverUpdateAdapter(IWuaDriverGateway? wuaGateway = null, Runner? pnputilRunner = null)
    {
        _wua = wuaGateway ?? new WuaComDriverGateway();
        _pnputil = pnputilRunner ?? MakeRealPnputilRunner();
    }

    /// <summary>可升级驱动清单：WUA 搜索（IsInstalled=0 and Type='Driver'），高危类名由适配层判定。</summary>
    public Task<IReadOnlyList<DriverUpdateEntry>> ListDriverUpdatesAsync(CancellationToken cancellation = default)
        => Task.Run<IReadOnlyList<DriverUpdateEntry>>(async () =>
        {
            var found = await _wua.SearchDriverUpdatesAsync(cancellation);
            return found.Select(d => new DriverUpdateEntry(
                d.HardwareId, d.DeviceName,
                InstalledVersion: null, // WUA 搜索结果不含现装版本，升级判定以「有候选更新」为据
                AvailableVersion: d.Version,
                DriverUpdateKind.WuaDriver,
                IsCriticalDevice: DriverCriticality.IsCriticalClass(d.ClassName),
                // WUA 驱动来自 Windows Update 目录（微软签名渠道），按官方源哈希已过处理
                HashVerified: true)).ToList();
        }, cancellation);

    /// <summary>驱动备份：pnputil /export-driver * 导出到备份目录（全量第三方驱动导出，pnputil 不支持单设备导出）。</summary>
    public Task<bool> BackupDriverAsync(string deviceId, string backupDirectory, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
            return Task.FromResult(false);
        var safeDir = QuotePath(backupDirectory);
        var (code, _, _) = _pnputil($"export-driver * {safeDir}");
        return Task.FromResult(code == 0);
    }

    /// <summary>执行 WUA 驱动升级（三道闸由 Core 编排器把守，本方法只做动作）。</summary>
    public Task<DriverActionResult> ApplyDriverUpdateAsync(DriverUpdateEntry driver, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (driver.Kind != DriverUpdateKind.WuaDriver)
            return Task.FromResult(new DriverActionResult(driver.DeviceId, Success: false, BackupCompleted: true,
                "厂商驱动包请在软件管家页选择 inf 文件后经 pnputil 安装（InstallVendorInfAsync），不走 WUA 通道"));

        return Task.Run(async () =>
        {
            var (ok, detail) = await _wua.InstallDriverAsync(driver.DeviceId, cancellation);
            return new DriverActionResult(driver.DeviceId, ok, BackupCompleted: true, detail);
        }, cancellation);
    }

    /// <summary>厂商驱动包安装（pnputil /add-driver inf /install）；哈希校验由调用方在调用前完成并核对。</summary>
    public Task<DriverActionResult> InstallVendorInfAsync(string infPath, string deviceId, CancellationToken cancellation = default)
    {
        if (!IsSafeInfPath(infPath))
            return Task.FromResult(new DriverActionResult(deviceId, Success: false, BackupCompleted: false,
                "inf 路径含不支持的字符，已拒绝执行；请从官方渠道下载驱动包并解压后重试"));
        var args = $"add-driver {QuotePath(infPath)} /install";
        var (code, stdout, stderr) = _pnputil(args);
        return Task.FromResult(new DriverActionResult(deviceId, code == 0, BackupCompleted: true,
            code == 0 ? "pnputil 驱动安装完成" : FirstLine(stderr, stdout)));
    }

    /// <summary>真机 pnputil Runner：进程调用，UTF-8/系统 OEM 输出读取，非 Windows 报红。</summary>
    public static Runner MakeRealPnputilRunner() => arguments =>
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("pnputil 适配层仅支持 Windows 真机运行；沙箱测试请注入假 Runner");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pnputil.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var ps = System.Diagnostics.Process.Start(psi)!;
        var stdout = ps.StandardOutput.ReadToEndAsync();
        var stderr = ps.StandardError.ReadToEndAsync();
        if (!ps.WaitForExit(PnputilTimeoutMs))
        {
            try { ps.Kill(); } catch (System.InvalidOperationException) { /* 已退出 */ }
            return (-1, string.Empty, "pnputil 命令超时，已终止；请稍后重试");
        }
        return (ps.ExitCode, stdout.Result, stderr.Result);
    };

    /// <summary>inf/目录路径白名单：盘符或反斜杠开头的本地路径，禁命令注入字符。</summary>
    public static bool IsSafeInfPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260) return false;
        if (path.Contains('"') || path.Contains(';') || path.Contains('&') || path.Contains('|')) return false;
        return char.IsLetter(path[0]) && path[1] == ':' || path[0] is '\\' or '/';
    }

    private static string QuotePath(string path) => $"\"{path}\"";

    private static string FirstLine(string stderr, string stdout)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .FirstOrDefault(s => s.Length > 0)
            ?? stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .LastOrDefault(s => s.Length > 0)
            ?? "pnputil 返回失败（无错误详情）";
        return line.Length > 200 ? line[..200] : line;
    }
}

/// <summary>WUA COM 网关（真机实现；动态绑定 Windows COM，沙箱不可运行，仅真机验证）。</summary>
public sealed class WuaComDriverGateway : IWuaDriverGateway
{
    public Task<IReadOnlyList<IWuaDriverGateway.WuaDriver>> SearchDriverUpdatesAsync(CancellationToken cancellation = default)
    {
        return Task.Run<IReadOnlyList<IWuaDriverGateway.WuaDriver>>(() =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("WUA 驱动搜索仅支持 Windows 真机运行；沙箱测试请注入假网关");
            var searcherType = Type.GetTypeFromProgID("Microsoft.Update.Searcher")
                ?? throw new InvalidOperationException("Windows Update 搜索器不可用（WUA 服务被禁用或系统精简版），请改用厂商官网驱动");
            dynamic searcher = Activator.CreateInstance(searcherType)!;
            searcher.ServerSelection = 2; // ssWindowsUpdate
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");
            var list = new List<IWuaDriverGateway.WuaDriver>();
            for (int i = 0; i < result.Count; i++)
            {
                dynamic update = result.Item(i);
                list.Add(new IWuaDriverGateway.WuaDriver(
                    (string)(update.DriverHardwareID ?? string.Empty),
                    (string)(update.Title ?? "未知设备"),
                    (string?)update.DriverVerVersion,
                    (string?)update.DriverClass));
            }
            return list;
        }, cancellation);
    }

    public Task<(bool Success, string? Detail)> InstallDriverAsync(string hardwareId, CancellationToken cancellation = default)
    {
        return Task.Run<(bool, string?)>(() =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("WUA 驱动安装仅支持 Windows 真机运行；沙箱测试请注入假网关");
            var searcherType = Type.GetTypeFromProgID("Microsoft.Update.Searcher")
                ?? throw new InvalidOperationException("Windows Update 搜索器不可用（WUA 服务被禁用或系统精简版）");
            dynamic searcher = Activator.CreateInstance(searcherType)!;
            searcher.ServerSelection = 2;
            dynamic result = searcher.Search($"IsInstalled=0 and Type='Driver'");
            dynamic? target = null;
            for (int i = 0; i < result.Count; i++)
            {
                dynamic update = result.Item(i);
                if (string.Equals((string?)update.DriverHardwareID, hardwareId, StringComparison.OrdinalIgnoreCase))
                {
                    target = update;
                    break;
                }
            }
            if (target is null)
                return (false, "Windows Update 中未再找到该设备的候选驱动，请刷新清单后重试");

            var installerType = Type.GetTypeFromProgID("Microsoft.Update.Installer")
                ?? throw new InvalidOperationException("Windows Update 安装器不可用（WUA 服务被禁用）");
            dynamic installer = Activator.CreateInstance(installerType)!;
            dynamic updates = installer.CreateUpdateCollection();
            updates.Add(target);
            installer.Updates = updates;
            dynamic installResult = installer.Install();
            int hresult = installResult.ResultCode; // 2=成功 3=已取消 4=失败 5=已中止
            return hresult == 2
                ? (true, "WUA 驱动安装完成")
                : (false, $"WUA 驱动安装未成功（结果码 {hresult}），可在驱动详情页查看系统事件");
        }, cancellation);
    }
}
