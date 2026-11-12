using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>扫描到的一个可见顶层窗口（只读快照，合规形态：不注入、不置顶、不改窗口）。</summary>
public sealed record DetectedPopupWindow(
    IntPtr Handle,
    string ProcessName,
    string WindowTitle,
    string WindowClass);

/// <summary>
/// 弹窗窗口扫描适配器（P8 弹窗管理真数据源）：
/// 只读枚举可见顶层窗口（EnumWindows + 标题/类名/进程名），把窗口文本快照交规则引擎判定。
/// 合规红线（断言 B3/N3）：本类绝不注入、不劫持、不置顶、不批量改他人窗口；
/// 唯一写动作是「用户逐个确认后对目标窗口发送 WM_CLOSE 优雅关闭消息」（关闭≠杀进程，应用可自行收尾）。
/// </summary>
public sealed class PopupWindowScanner
{
    private const int WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>枚举当前所有可见顶层窗口（进程名/标题/类名）。失败返回空清单，不假数据。</summary>
    public IReadOnlyList<DetectedPopupWindow> GetVisibleTopLevelWindows()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<DetectedPopupWindow>();

        var handles = new List<IntPtr>();
        if (!EnumWindows((h, _) =>
            {
                if (IsWindowVisible(h)) handles.Add(h);
                return true; // 继续枚举
            }, IntPtr.Zero))
        {
            return Array.Empty<DetectedPopupWindow>();
        }

        var results = new List<DetectedPopupWindow>(handles.Count);
        foreach (var handle in handles)
        {
            try
            {
                var title = ReadWindowTitle(handle);
                var className = ReadWindowClass(handle);
                var processName = ReadProcessName(handle);
                if (processName.Length == 0) continue;
                results.Add(new DetectedPopupWindow(handle, processName, title, className));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 进程已退出/访问被拒：跳过该窗口，不中断枚举
            }
        }
        return results;
    }

    /// <summary>对用户逐个确认的目标窗口发送 WM_CLOSE（优雅关闭请求；应用可拒绝或自行收尾）。</summary>
    public bool TryCloseGracefully(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows() || handle == IntPtr.Zero) return false;
        return PostMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private static string ReadWindowTitle(IntPtr hWnd)
    {
        var len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ReadWindowClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private static string ReadProcessName(IntPtr hWnd)
    {
        if (GetWindowThreadProcessId(hWnd, out var pid) == 0 || pid == 0) return string.Empty;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty; // 进程刚好退出
        }
    }
}
