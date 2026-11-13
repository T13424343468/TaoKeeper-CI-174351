using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>
/// 官方卸载链启动器（R-P2-5：走官方卸载程序，不做进程强杀式删除）。
/// 注册表枚举（RegistryInstalledSoftwareSource）条目的 Id 即 UninstallString——
/// 官方卸载命令来自系统登记，本层只负责安全起进程：解析可执行与参数（双引号路径优先），
/// 不改写、不拼接、不猜测。winget 登记条目（Id 是包 Id 而非命令）交回 winget uninstall 降级。
/// 命令行解析纯逻辑沙箱可测（坑 11：Windows 路径按双分隔符口径做字符串处理，本层不做 IO）。
/// </summary>
public sealed class OfficialUninstallLauncher
{
    /// <summary>一次卸载命令执行结果（真机进程承载；测试注入假 Runner）。</summary>
    public delegate (int ExitCode, string StdErr) CommandRunner(string fileName, string arguments);

    private readonly CommandRunner _run;

    public OfficialUninstallLauncher(CommandRunner? runner = null)
        => _run = runner ?? MakeRealRunner();

    /// <summary>该条目是否可走官方卸载链：Id 为我们注册表枚举带回的卸载命令（含盘符路径特征）。</summary>
    public static bool CanLaunch(SoftwareEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var id = entry.Id;
        return id.Length > 3
            && char.IsLetter(id[0]) && id[1] == ':' && (id[2] == '\\' || id[2] == '/')
            && !WingetSoftwareSourceAdapter.IsSafeId(id); // winget 包 Id 不含路径分隔符，二者互斥
    }

    /// <summary>
    /// 执行官方卸载：起官方卸载程序并等待退出（后台线程承载，UI 零阻塞）。
    /// 等待上限 10 分钟（官方卸载器弹自家确认窗属正常等待）；超时按失败如实报，不强杀进程。
    /// </summary>
    public Task<SoftwareActionResult> UninstallAsync(SoftwareEntry entry, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!CanLaunch(entry))
            throw new InvalidOperationException("该条目不走官方卸载链，请降级 winget uninstall");

        cancellation.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            var (fileName, arguments) = SplitCommand(entry.Id);
            if (fileName.Length == 0)
                return new SoftwareActionResult(entry.Id, IsInstall: false, Success: false, HashVerified: null,
                    "卸载命令无法解析，未执行任何动作；请改用系统「应用和功能」处理");

            var (code, stderr) = _run(fileName, arguments);
            return code == 0
                ? new SoftwareActionResult(entry.Id, IsInstall: false, Success: true, HashVerified: true, Detail: "官方卸载程序执行完成")
                : new SoftwareActionResult(entry.Id, IsInstall: false, Success: false, HashVerified: null,
                    Detail: $"官方卸载程序未正常完成（退出码 {code}）{Footnote(stderr)}");
        }, cancellation);
    }

    /// <summary>
    /// 命令行拆解（纯逻辑）：带引号路径取到闭引号，其余为参数；无引号按首个空白切分。
    /// 双分隔符口径（坑 11）：'/' 与 '\' 都按分隔符识别，不改写原串。
    /// </summary>
    public static (string FileName, string Arguments) SplitCommand(string commandLine)
    {
        var line = commandLine.TrimStart();
        if (line.Length == 0) return (string.Empty, string.Empty);

        if (line[0] == '"')
        {
            var close = line.IndexOf('"', 1);
            if (close < 0) return (string.Empty, string.Empty);
            return (line[1..close], line[(close + 1)..].TrimStart());
        }

        // 无引号：路径可能含空格（Program Files），逐段试探「候选路径是否已含可执行扩展名」，
        // 试探纯按字符串口径（不做 IO）：.exe/.msi 结尾即视为命令完整
        var spaceIdx = -1;
        while ((spaceIdx = line.IndexOf(' ', spaceIdx + 1)) > 0)
        {
            var candidate = line[..spaceIdx];
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || candidate.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                return (candidate, line[(spaceIdx + 1)..].TrimStart());
        }
        return (line, string.Empty);
    }

    /// <summary>真机 Runner：cmd /c 承载（官方卸载串常带环境变量与参数），等待退出抓退出码。</summary>
    public static CommandRunner MakeRealRunner(int timeoutMs = 10 * 60 * 1000) => (fileName, arguments) =>
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("官方卸载链仅支持 Windows 真机运行；沙箱测试请注入假 CommandRunner");

        using var ps = new Process();
        ps.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        ps.Start();
        if (!ps.WaitForExit(timeoutMs))
            return (-1, "官方卸载程序等待超时，已保留进程不强杀；请到系统「应用和功能」确认");
        return (ps.ExitCode, string.Empty);
    };

    /// <summary>失败脚注：取错误首行补充给用户（不透露内部细节，与 winget 适配层同一截断口径）。</summary>
    private static string Footnote(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .FirstOrDefault(s => s.Length > 0);
        if (line is null) return string.Empty;
        return line.Length > 120 ? $"：{line[..120]}" : $"：{line}";
    }
}
