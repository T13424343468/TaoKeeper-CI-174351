using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>
/// winget 软件源适配器（ISoftwareSourceAdapter 的 Windows 真机实现）。
/// 全部动作经 winget CLI 完成：winget 本身按 manifest 校验安装包哈希，安装成功即视为哈希已过；
/// 失败输出含哈希失配标记时回填 HashVerified=false，其余失败回填 null（未核验），不猜测。
/// 非 Windows 运行时报红（PlatformNotSupportedException），不静默跳过——与 Core 互操作同一规则。
/// 沙箱测试用注入 Runner 的假进程验证参数拼装与结果映射。
/// </summary>
public sealed class WingetSoftwareSourceAdapter : ISoftwareSourceAdapter
{
    /// <summary>一次 winget 进程调用结果（退出码 + 输出）。</summary>
    public delegate (int ExitCode, string StdOut, string StdErr) Runner(string arguments);

    private const string HashMismatchDetail = SoftwareActionResult.HashMismatchDetail;
    private readonly Runner _run;

    /// <summary>最近一次调用的完整参数（界面「拦截可回溯」展示用）。
    /// 由后台线程池写入、界面线程读取，volatile 保证跨线程可见。</summary>
    private volatile string? _lastArguments;

    public WingetSoftwareSourceAdapter(Runner? runner = null, string wingetPath = "winget")
    {
        _run = runner ?? MakeRealRunner(wingetPath);
    }

    /// <summary>最近一次调用的完整参数（winget 进程在后台线程执行，读取侧经 volatile 保证可见）。</summary>
    public string? LastArguments => _lastArguments;

    /// <summary>
    /// 已安装清单：winget list（升级判定一律以 winget upgrade 为准，本方法只供清册展示）。
    /// 真机 winget 进程常需数十秒，进程调用统一经 Task.Run 在后台线程承载，避免卡住界面线程；
    /// 取消令牌在进出进程调用时检查（Runner 为同步契约，无法中断运行中的进程）。
    /// </summary>
    public Task<IReadOnlyList<SoftwareEntry>> ListInstalledAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            var (code, stdout, stderr) = _run("list --accept-source-agreements");
            if (code < 0)
                throw new InvalidOperationException($"winget list 调用失败：{FirstErrorLine(stderr, string.Empty)}");
            return WingetOutputParser.ParseList(stdout);
        }, cancellation);
    }

    /// <summary>
    /// 可升级清单：winget upgrade --include-unknown（含 winget 读不出版本的条目，升级判定统一走这里）。
    /// 同样后台线程承载，口径同已安装清单。
    /// </summary>
    public Task<IReadOnlyList<SoftwareEntry>> ListUpgradesAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            var (code, stdout, stderr) = _run("upgrade --include-unknown --accept-source-agreements");
            if (code < 0)
                throw new InvalidOperationException($"winget upgrade 调用失败：{FirstErrorLine(stderr, string.Empty)}");
            return WingetOutputParser.ParseUpgrades(stdout);
        }, cancellation);
    }

    /// <summary>
    /// 安装/升级。哈希已判失配的条目在进 winget 之前就拒绝（硬规则：确认也不放行）；
    /// allowSilent=false 时不开 --silent，让用户在安装器里看得见过程。
    /// </summary>
    public Task<SoftwareActionResult> InstallAsync(SoftwareEntry software, bool allowSilent, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(software);
        if (!IsSafeId(software.Id))
            return Task.FromResult(new SoftwareActionResult(software.Id, IsInstall: true, Success: false, HashVerified: null,
                "软件 Id 含不支持的字符，已拒绝执行；请在详情页核对该条目"));

        if (software.HashVerified == false)
            return Task.FromResult(new SoftwareActionResult(software.Id, IsInstall: true, Success: false,
                HashVerified: false, Detail: HashMismatchDetail));

        var args = $"upgrade --id \"{software.Id}\" --version \"{software.AvailableVersion}\""
                 + " --accept-package-agreements --accept-source-agreements"
                 + (allowSilent ? " --silent" : string.Empty)
                 + (software.Source == SoftwareSourceKind.WingetCommunity ? " --source winget" : string.Empty);

        return Task.Run(() =>
        {
            _lastArguments = args;
            var (code, stdout, stderr) = _run(args);

            if (code == 0)
                return new SoftwareActionResult(software.Id, IsInstall: true, Success: true,
                    HashVerified: true, Detail: "winget 升级完成");

            return new SoftwareActionResult(software.Id, IsInstall: true, Success: false,
                HashVerified: LooksLikeHashMismatch(stdout, stderr) ? false : null,
                Detail: FirstErrorLine(stderr, stdout));
        }, cancellation);
    }

    /// <summary>卸载：走 winget uninstall（系统正规卸载入口），不开静默，过程对用户可见。</summary>
    public Task<SoftwareActionResult> UninstallAsync(SoftwareEntry software, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(software);
        if (!IsSafeId(software.Id))
            return Task.FromResult(new SoftwareActionResult(software.Id, IsInstall: false, Success: false, HashVerified: null,
                "软件 Id 含不支持的字符，已拒绝执行；请在详情页核对该条目"));

        var args = $"uninstall --id \"{software.Id}\" --accept-source-agreements";

        return Task.Run(() =>
        {
            _lastArguments = args;
            var (code, _, stderr) = _run(args);

            if (code == 0)
                return new SoftwareActionResult(software.Id, IsInstall: false, Success: true,
                    HashVerified: true, Detail: "已卸载");

            return new SoftwareActionResult(software.Id, IsInstall: false, Success: false,
                HashVerified: null, Detail: FirstErrorLine(stderr, string.Empty));
        }, cancellation);
    }

    /// <summary>真机 Runner：起 winget 进程等退出（UTF-8 输出，超时按失败不挂死）；非 Windows 直接报红。</summary>
    public static Runner MakeRealRunner(string wingetPath = "winget", int timeoutMs = 10 * 60 * 1000) => arguments =>
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("winget 适配层仅支持 Windows 真机运行；沙箱测试请注入假 Runner");

        using var ps = new Process();
        ps.StartInfo = new ProcessStartInfo
        {
            FileName = wingetPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ps.Start();
        var stdout = ps.StandardOutput.ReadToEndAsync();
        var stderr = ps.StandardError.ReadToEndAsync();
        if (!ps.WaitForExit(timeoutMs))
        {
            try { ps.Kill(); } catch (System.InvalidOperationException) { /* 已退出 */ }
            return (-1, string.Empty, "winget 命令超时，已终止；请稍后重试或改手动处理");
        }
        return (ps.ExitCode, stdout.Result, stderr.Result);
    };

    /// <summary>Id 白名单：字母数字与常见分隔符，杜绝参数注入（引号、分号、换行等一律拒绝）。</summary>
    public static bool IsSafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256) return false;
        foreach (var c in id)
        {
            bool ok = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or ' ' or '(' or ')';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>哈希失配标记：winget 校验安装包哈希失败的输出特征（英文两种表述 + 中文环境）。</summary>
    internal static bool LooksLikeHashMismatch(string stdout, string stderr)
    {
        var text = $"{stdout}\n{stderr}".ToLowerInvariant();
        return text.Contains("hash mismatch") || text.Contains("hash verification failed") || text.Contains("哈希");
    }

    /// <summary>失败文案：取第一条非空错误行，截断到 200 字，不透露内部调用细节。</summary>
    private static string FirstErrorLine(string stderr, string stdout)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .FirstOrDefault(s => s.Length > 0)
            ?? stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .LastOrDefault(s => s.Length > 0)
            ?? "winget 返回失败（无错误详情）";
        return line.Length > 200 ? line[..200] : line;
    }
}
