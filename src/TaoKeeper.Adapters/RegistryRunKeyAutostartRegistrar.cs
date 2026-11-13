using Microsoft.Win32;
using TaoKeeper.Core.Settings;

namespace TaoKeeper.Adapters;

/// <summary>
/// 托盘开机自启注册表实现（R-P12-9）：HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// 写入「值名 TaoKeeper = 带引号 exe 全路径」。仅当前用户（不碰 HKLM、不提权）；
/// 值命令行构造为纯函数便于沙箱测试；非 Windows 平台如实失败（不静默假成功）。
/// </summary>
public sealed class RegistryRunKeyAutostartRegistrar : IAutostartRegistrar
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>自启项值名（与 WiX 打包、卸载清理保持同一口径；改名须三处同步）。</summary>
    public const string ValueName = "TaoKeeper";

    /// <summary>纯函数：由 exe 路径构造 Run 键命令行（路径含空格须整体加引号）。</summary>
    public static string RunCommandFor(string exePath) => $"\"{exePath}\"";

    public bool IsRegistered()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch (System.Exception)
        {
            return false; // 注册表读不到按未注册如实处理，调用方以注册动作为准
        }
    }

    public bool TryRegister(out string error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "开机自启注册仅 Windows 真机可用。";
            return false;
        }
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
            {
                error = "无法定位本程序可执行文件，未写入自启项。";
                return false;
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(ValueName, RunCommandFor(exe));
            error = string.Empty;
            return true;
        }
        catch (System.Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            error = $"自启项写入未完成：{ex.Message}（未做半截改动）。";
            return false;
        }
    }

    public bool TryUnregister(out string error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "开机自启注销仅 Windows 真机可用。";
            return false;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is not null && key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            error = string.Empty;
            return true; // 未注册时注销按幂等成功处理
        }
        catch (System.Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            error = $"自启项注销未完成：{ex.Message}。";
            return false;
        }
    }
}
