using TaoKeeper.AppKernel.Theming;

namespace TaoKeeper.AppKernel.Settings;

/// <summary>挂件显示内容（原型：想看哪样勾哪样）。</summary>
public enum WidgetContent
{
    Memory = 0,      // 内存
    JunkPending,     // 垃圾待清
    DiskFree,        // 磁盘余量
}

/// <summary>挂件大小两档（原型：位置随手拖动）。</summary>
public enum WidgetSize
{
    Standard = 0,    // 标准
    Compact,         // 紧凑
}

/// <summary>
/// 应用设置模型，字段、默认值与锁定语义严格按原型 v19 设置页。
/// 红线项（三层官方软件源 / 装前哈希校验 / 清理先进回收站）不可改，
/// 由 <see cref="AppSettingsGuard"/> 强制；默认值与原型开关初始态逐项一致。
/// </summary>
public sealed class AppSettings
{
    // ── 通用 ──
    public ThemeName Theme { get; set; } = ThemeName.Shuimo;   // 界面主题：全局即时生效
    public bool LaunchOnStartup { get; set; } = true;          // 开机自动启动：随 Windows 启动，常驻极轻
    public bool AutoCheckupOnStartup { get; set; } = true;     // 开机后自动体检：静默跑一遍，只报变化不刷屏

    // ── 软件管家 ──
    public string DefaultInstallDir { get; set; } = @"D:\TaoKeeper\Soft"; // 默认安装位置：每款软件装前可单独改
    public bool SilentUpgrade { get; set; } = false;           // 静默升级：默认关，开启后仅「可静默」类自动升级
    public bool CreateShortcutAfterInstall { get; set; } = true; // 装完自动建桌面快捷方式：默认开，也可装完再选
    public bool RecordInstallLedger { get; set; } = true;      // 装完记入安装账：默认开，每笔可回溯
    public bool ShowDesktopWidget { get; set; } = true;        // 显示桌面挂件：关闭即零后台残留，自启同步注销
    public int MemoryAlertPercent { get; set; } = 85;          // 内存告警线：物理内存超过 85% 才提醒，可调可关（0=关）
    public double DiskFreeAlertGb { get; set; } = 10.0;        // 磁盘余量提醒：C 盘剩余不足 10 GB 时提醒，可调

    // ── 清理与存储 ──
    public bool StorageSenseEnabled { get; set; } = false;     // 存储感知定时清理：默认关，到点只出确认预览
    public bool QuickScanByDefault { get; set; } = true;       // 快扫默认开启：打开磁盘清理即快扫

    // ── 桌面挂件 ──
    public HashSet<WidgetContent> WidgetContents { get; set; } = new() { WidgetContent.Memory, WidgetContent.JunkPending };
    public WidgetSize WidgetSize { get; set; } = WidgetSize.Standard;

    /// <summary>设置副本（含集合深拷贝），供确认浮层与持久化比对。</summary>
    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.WidgetContents = new HashSet<WidgetContent>(WidgetContents);
        return c;
    }
}

/// <summary>
/// 红线锁定项（原型页面对应位置为「红线锁定」pill，任何设置写入路径不得绕过）：
/// ① 三层官方软件源：winget / GitHub / 官网直链
/// ② 装前哈希校验：逐包校验，失配即拦
/// ③ 清理先进回收站：一切清理都可还原
/// </summary>
public static class AppSettingsGuard
{
    public const string OfficialSourceLockedMessage = "三层官方软件源为红线，不可关闭或更换渠道";
    public const string HashCheckLockedMessage = "装前哈希校验为红线，不可关闭";
    public const string RecycleBinLockedMessage = "清理先进回收站为红线，不可关闭";

    /// <summary>红线项不作为设置字段存在，只以常量暴露文案；任何序列化都不含红线开关。</summary>
    public static readonly string[] LockedPills =
    {
        OfficialSourceLockedMessage,
        HashCheckLockedMessage,
        RecycleBinLockedMessage,
    };
}
