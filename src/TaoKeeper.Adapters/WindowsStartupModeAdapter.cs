using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>
/// Windows 启动项三档真实适配器（R-P5-6，G8 落点）：
/// 禁用/立即启动复用既有 <see cref="WindowsStartupControlAdapter"/>（先快照后摘除/按快照恢复，可回滚）；
/// 延迟启动=注册系统计划任务（ONLOGON + /DELAY 延时拉起，官方计划任务接口，零注入零劫持）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupModeAdapter : IStartupModeAdapter
{
    private readonly WindowsStartupControlAdapter _control;

    public WindowsStartupModeAdapter(WindowsStartupControlAdapter control)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
    }

    public async Task<StartupModeResult> SetModeAsync(string itemId, StartupLaunchMode mode, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var items = await _control.ListStartupItemsAsync(cancellation).ConfigureAwait(false);
        var item = items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
            return new StartupModeResult(itemId, mode, false, "启动项不存在或已被移除，未做任何改动。");

        switch (mode)
        {
            case StartupLaunchMode.Disabled:
            {
                var r = await _control.ToggleStartupAsync(itemId, enable: false, cancellation).ConfigureAwait(false);
                return new StartupModeResult(itemId, mode, r.Success, r.Detail);
            }
            case StartupLaunchMode.Immediate:
            {
                // 立即启动=恢复正常自启；若此前设过延迟任务，先移除延迟任务
                RemoveDelayTask(itemId);
                var r = await _control.ToggleStartupAsync(itemId, enable: true, cancellation).ConfigureAwait(false);
                return new StartupModeResult(itemId, mode, r.Success, r.Success ? "已恢复立即启动" : r.Detail);
            }
            case StartupLaunchMode.Delayed:
            {
                var command = item.Command;
                if (string.IsNullOrWhiteSpace(command))
                    return new StartupModeResult(itemId, mode, false, "该启动项无可执行命令（来源不支持延迟启动），未做任何改动。");

                // 先禁用原自启（走既有快照通道可回滚），再注册延迟拉起计划任务
                var disable = await _control.ToggleStartupAsync(itemId, enable: false, cancellation).ConfigureAwait(false);
                if (!disable.Success)
                    return new StartupModeResult(itemId, mode, false, $"延迟前禁用未完成：{disable.Detail}（未注册延迟任务）");

                try
                {
                    CreateDelayTask(itemId, command);
                    return new StartupModeResult(itemId, mode, true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    // 延迟任务注册失败：如实回滚为启用态，不留半截状态
                    await _control.ToggleStartupAsync(itemId, enable: true, cancellation).ConfigureAwait(false);
                    return new StartupModeResult(itemId, mode, false, $"延迟任务注册失败（已回滚为启用）：{ex.Message}");
                }
            }
            default:
                return new StartupModeResult(itemId, mode, false, "未知档位，未做任何改动。");
        }
    }

    private static void CreateDelayTask(string itemId, string command)
    {
        var taskName = DelayTaskName(itemId);
        // /SC ONLOGON + /DELAY 官方计划任务延时口径：登录完成后延时 2 分钟再拉起
        var psi = new ProcessStartInfo("schtasks.exe",
            $"/Create /F /SC ONLOGON /DELAY 0002:00 /TN \"{taskName}\" /TR \"{command.Replace("\"", "\\\"")}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("schtasks.exe 启动失败");
        process.WaitForExit(30000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"schtasks.exe 退出码 {process.ExitCode}（可能需要管理员权限）");
    }

    private static void RemoveDelayTask(string itemId)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Delete /F /TN \"{DelayTaskName(itemId)}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(30000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // 旧延迟任务不存在/移除失败不挡恢复立即启动；残留任务由卸载善后链清理
        }
    }

    internal static string DelayTaskName(string itemId) => "TaoKeeperDelay-" + itemId;
}
