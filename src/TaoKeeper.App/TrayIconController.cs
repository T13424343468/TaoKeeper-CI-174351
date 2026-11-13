using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Settings;

namespace TaoKeeper.App;

/// <summary>
/// 系统托盘控制器（R-P12-9）：Shell_NotifyIcon 自实现，不引第三方包（离线仓口径）。
/// 左键=打开主窗口；右键菜单五项目严格按原型逐字：打开道管家 / 立即清理 / 显示挂件 /
/// 开机自启 / 退出（分隔线隔退出，不擅自加项）。
/// 「立即清理」走与主程序、挂件同一 CleanupExecutor（同回收站语义、同清理账）；
/// 「开机自启」经 IAutostartRegistrar 落 HKCU Run 键，与设置页托盘项同源；
/// 「显示挂件」打开仓内真实挂件页（桌面浮窗挂件未落地，接线上如实说明，不假显隐）。
/// 道系边界：执行结果经托盘气泡如实上报，不假成功；回调窗口为 message-only 窗口，不占任务栏。
/// </summary>
internal static class TrayIconController
{
    // ── 宿主接线（组合根装配；未接线处按未测态如实兜底，不假动作） ──

    /// <summary>垃圾候选提供者（与挂件页同一最近扫描缓存；未扫描为空清单）。</summary>
    internal static Func<System.Collections.Generic.IReadOnlyList<CleanupActionItem>>? JunkCandidatesProvider { get; set; }

    /// <summary>清理执行（与主程序同一 CleanupCoordinator，回收站语义）。</summary>
    internal static Func<System.Collections.Generic.IReadOnlyList<CleanupActionItem>, Task<CleanupBatchResult>>? CleanupExecutorProvider { get; set; }

    /// <summary>打开主窗口（App 接线：显示并激活主窗）。</summary>
    internal static Action? OpenMainWindowHandler { get; set; }

    /// <summary>显示挂件（App 接线：切到仓内真实挂件页；桌面浮窗挂件待接线）。</summary>
    internal static Action? ShowWidgetHandler { get; set; }

    /// <summary>自启注册器（组合根接线；非 Windows 为 null，自启动作如实不可用）。</summary>
    internal static IAutostartRegistrar? AutostartRegistrar { get; set; }

    /// <summary>托盘常驻态（设置页确认后经 ApplySettings 更新；默认 true 与原型一致）。</summary>
    internal static bool Resident { get; private set; } = true;

    /// <summary>托盘菜单「退出」已触发（主窗 Closed 据此不再取消关闭，避免常驻语义打转）。</summary>
    internal static bool QuitRequested { get; private set; }

    private static bool _created;
    private static IntPtr _msgWindow;
    private static IntPtr _icon;

    /// <summary>委托实例静态保活：原生回调窗口生命周期内指向的托管方法不得被回收。</summary>
    private static readonly WndProcDelegate TrayWndProcDelegate = TrayWndProc;

    // ── 对外动作 ──

    /// <summary>应用启动后创建托盘图标（重复调用幂等；非 Windows/初始化失败静默降级为无托盘，不挡启动）。</summary>
    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindows() || _created) return;
        try
        {
            _msgWindow = CreateMessageWindow();
            if (_msgWindow == IntPtr.Zero) return;
            _created = AddOrModifyIcon(NIM.ADD);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>移除托盘图标（退出或窗口随退出关闭时调用；幂等）。</summary>
    internal static void RemoveIcon()
    {
        if (!_created || _msgWindow == IntPtr.Zero) return;
        var data = CreateNotifyData(); // NIM.DELETE 只读 hWnd/uID，其余字段按默认构造即可
        Shell_NotifyIcon(NIM.DELETE, ref data);
        _created = false;
    }

    /// <summary>
    /// 设置页确认后应用托盘两项（R-P12-9）：常驻态即时生效；
    /// 开机自启与注册表实际对齐（差量注册/注销，幂等）。返回空=无事发生，非空=如实错误文案。
    /// </summary>
    internal static string ApplySettings(bool trayAutostart, bool trayResident)
    {
        Resident = trayResident;
        return SyncAutostart(trayAutostart);
    }

    /// <summary>把自启项对齐到目标态（菜单与设置页共用同源逻辑；未接线返回空串=本会话不适用）。</summary>
    private static string SyncAutostart(bool target)
    {
        var reg = AutostartRegistrar;
        if (reg is null) return string.Empty;
        if (reg.IsRegistered() == target) return string.Empty;
        return target ? reg.TryRegister(out var err) ? string.Empty : err
                      : reg.TryUnregister(out var err2) ? string.Empty : err2;
    }

    // ── 菜单动作（id 与 AppendMenu 顺序一一对应） ──

    private const uint MENU_OPEN = 1;
    private const uint MENU_CLEAN = 2;
    private const uint MENU_WIDGET = 3;
    private const uint MENU_AUTOSTART = 4;
    private const uint MENU_QUIT = 6; // 5 为分隔线占位，不参与分派

    private static void OnMenuAction(uint id)
    {
        switch (id)
        {
            case MENU_OPEN:
                OpenMainWindowHandler?.Invoke();
                break;

            case MENU_CLEAN:
                RunCleanFromTray();
                break;

            case MENU_WIDGET:
                // 桌面浮窗挂件尚未落地（仓内唯一真实挂件呈现为挂件页）：如实打开挂件页，不假显隐
                ShowWidgetHandler?.Invoke();
                break;

            case MENU_AUTOSTART:
            {
                var reg = AutostartRegistrar;
                if (reg is null)
                {
                    ShowBalloon("开机自启暂不可用", "自启通道未接线（本会话），未做任何改动。");
                    break;
                }
                var target = !reg.IsRegistered(); // 与注册表实际对齐取反：菜单态即注册表真相
                var err = SyncAutostart(target);
                ShowBalloon("开机自启已" + (target ? "开启" : "关闭"),
                    err.Length == 0 ? "即时生效 · 与托盘设置同源（R-P12-9）" : err);
                break;
            }

            case MENU_QUIT:
                QuitRequested = true;
                RemoveIcon();
                if (_msgWindow != IntPtr.Zero) DestroyWindow(_msgWindow);
                _msgWindow = IntPtr.Zero;
                Microsoft.UI.Xaml.Application.Current.Exit();
                break;
        }
    }

    /// <summary>托盘「立即清理」：范围=主程序已选集合（与挂件、总览同一缓存与执行链，R-P12-9）。</summary>
    private static async void RunCleanFromTray()
    {
        var candidates = JunkCandidatesProvider?.Invoke() ?? Array.Empty<CleanupActionItem>();
        if (CleanupExecutorProvider is null)
        {
            ShowBalloon("清理未执行", "清理执行通道未接线；未动任何文件。");
            return;
        }
        if (candidates.Count == 0)
        {
            ShowBalloon("暂无可清理项", "垃圾明细未就绪：先在主程序完成一次扫描（或稍后再试）。");
            return;
        }

        try
        {
            var items = candidates.Select(i => i with { Confirmed = true }).ToList(); // 托盘入口即确认，闸门仍拒受保护路径
            var batch = await CleanupExecutorProvider(items);
            ShowBalloon("清理完成",
                $"{batch.SucceededCount} 项（约 {batch.SucceededBytes / 1024.0 / 1024:F1} MB）已进回收站可还原；未成 {batch.FailedCount} 项、拒绝 {batch.RefusedCount} 项。");
        }
        catch (Exception ex)
        {
            ShowBalloon("清理未完成", $"{ex.Message}（目标文件未动，可重试）");
        }
    }

    /// <summary>托盘气泡上报（NIF_INFO；文案随 NOTIFYICONDATA 原生展示，不另弹窗打断操作）。</summary>
    private static void ShowBalloon(string title, string message)
    {
        if (!_created) return;
        var data = CreateNotifyData();
        data.szInfoTitle = title;
        data.szInfo = message;
        data.uFlags |= 0x10; // NIF_INFO：纯文字气泡上报，不打断当前操作
        data.dwInfoFlags = 0;
        Shell_NotifyIcon(NIM.MODIFY, ref data);
    }

    // ── Win32：message-only 回调窗口 + Shell_NotifyIcon + 托盘菜单 ──

    private const uint WM_APP_TRAY = 0x8001; // WM_APP 段自留量，无碰撞

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [Flags]
    private enum NIM : uint { ADD = 0, MODIFY = 1, DELETE = 2 }

    [Flags]
    private enum NIF : uint { MESSAGE = 1, ICON = 2, TIP = 4, INFO = 0x10 }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(NIM dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_CHECKED = 0x00000008;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const string ClassName = "TaoKeeperTrayMsg";

    /// <summary>message-only 回调窗口：不占任务栏，只收托盘消息（parent=HWND_MESSAGE）。</summary>
    private static IntPtr CreateMessageWindow()
    {
        var wc = new WNDCLASS
        {
            lpfnWndProc = TrayWndProcDelegate,
            hInstance = GetModuleHandleW(null),
            lpszClassName = ClassName,
        };
        RegisterClassW(ref wc);
        return CreateWindowExW(0, ClassName, ClassName, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    private static NOTIFYICONDATA CreateNotifyData()
    {
        // 图标：优先取程序资源 #1（真机出包换自绘盾形资源，见交接清单），缺省回退系统应用图标
        _icon = LoadIconW(GetModuleHandleW(null), new IntPtr(1));
        if (_icon == IntPtr.Zero) _icon = LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _msgWindow,
            uID = 1,
            uFlags = (uint)(NIF.MESSAGE | NIF.ICON | NIF.TIP),
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = "道管家 · TaoKeeper",
        };
    }

    private static bool AddOrModifyIcon(NIM op)
    {
        var data = CreateNotifyData();
        return Shell_NotifyIcon(op, ref data);
    }

    private static IntPtr TrayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (mouse)
            {
                case 0x0202: // WM_LBUTTONUP：左键=打开主窗口（原型口径）
                    OpenMainWindowHandler?.Invoke();
                    break;
                case 0x0205: // WM_RBUTTONUP：右键呼托盘菜单
                    ShowTrayMenu();
                    break;
            }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>托盘菜单：五项目逐字对原型，分隔线隔退出；「开机自启」勾选态=注册表实际（不假勾）。</summary>
    private static void ShowTrayMenu()
    {
        if (_msgWindow == IntPtr.Zero) return;

        // TrackPopupMenu 前置 SetForegroundWindow：托盘菜单不收外的标准修法
        SetForegroundWindow(_msgWindow);
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MF_STRING, MENU_OPEN, "打开道管家");
            AppendMenuW(menu, MF_STRING, MENU_CLEAN, "立即清理");
            AppendMenuW(menu, MF_STRING, MENU_WIDGET, "显示挂件");
            var autostartOn = AutostartRegistrar?.IsRegistered() == true;
            AppendMenuW(menu, MF_STRING | (autostartOn ? MF_CHECKED : 0), MENU_AUTOSTART, "开机自启");
            AppendMenuW(menu, MF_SEPARATOR, 5, null);
            AppendMenuW(menu, MF_STRING, MENU_QUIT, "退出");

            GetCursorPos(out var p);
            var chosen = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, _msgWindow, IntPtr.Zero);
            if (chosen != 0) OnMenuAction((uint)chosen);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }
}
