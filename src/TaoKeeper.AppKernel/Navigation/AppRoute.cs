namespace TaoKeeper.AppKernel.Navigation;

/// <summary>
/// 导航路由：需求稿 G6 门面收简为唯一 UI 基线（侧栏一级恒 7 项：
/// 总览/体检/清理/软件管家/加速/工具箱/设置）。
/// 顺序不可改动；工具箱为二级收纳（弹窗管理/存储感知/桌面挂件等经工具箱进入）。
/// </summary>
public enum AppRoute
{
    Home = 0,     // 总览
    Checkup,      // 系统体检
    Disk,         // 磁盘清理
    SwStore,      // 软件管家
    Boost,        // 性能加速
    Tools,        // 工具箱（二级收纳：驱动/弹窗管理/存储感知/桌面挂件等）
    Settings,     // 设置

    // —— 工具箱二级收纳路由（G6：经工具箱卡片进入，不入侧栏导航条）——
    Mem,          // 内存管理
    Driver,       // 驱动安装升级
    Popup,        // 弹窗管理
    Storage,      // 存储感知
    Widget,       // 桌面挂件
    Design,       // 设计说明（开发期保留，交付版隐藏）

    // —— 场景入口路由（P-1 深水四页；不入导航条，经场景入口 SelectRoute 跳转，V-A6 侧栏恒 7 项）——
    SandboxPreview,   // P13 先试后行（沙箱预演）
    ResidueDetect,    // P14 残留侦探（引用图）
    IncidentReplay,   // P15 事发回放（归因报告）
    SnapshotChain,    // P16 时光回溯（快照链）
    DiskHardcore,     // P-6 硬核四项（数据找回+硬盘诊断，工具箱误删恢复卡入口 R-P10-5）
}

public static class AppRouteCatalog
{
    /// <summary>导航顺序与需求稿 G6 一一对应；一级恒 7 项，页面 id 与中文名同源需求稿。</summary>
    public static readonly (AppRoute Route, string PageId, string Title)[] Items =
    {
        (AppRoute.Home,    "home",    "总览"),
        (AppRoute.Checkup, "checkup", "系统体检"),
        (AppRoute.Disk,    "disk",    "磁盘清理"),
        (AppRoute.SwStore, "swstore", "软件管家"),
        (AppRoute.Boost,   "boost",   "性能加速"),
        (AppRoute.Tools,   "tools",   "工具箱"),
        (AppRoute.Settings,"settings","设置"),

        // —— 二级收纳页（G6：经工具箱等入口进入，不入侧栏导航条）——
        (AppRoute.Mem,     "mem",     "内存管理"),
        (AppRoute.Driver,  "driver",  "驱动安装升级"),
        (AppRoute.Popup,   "popup",   "弹窗管理"),
        (AppRoute.Storage, "storage", "存储感知"),
        (AppRoute.Widget,  "widget",  "桌面挂件"),
        (AppRoute.Design,  "design",  "设计说明"),
    };

    /// <summary>交付版导航条=一级恒 7 项（G6 铁律）；二级与设计说明均不出现。</summary>
    public static readonly AppRoute[] NavBarRoutes =
        new[] { AppRoute.Home, AppRoute.Checkup, AppRoute.Disk, AppRoute.SwStore,
                AppRoute.Boost, AppRoute.Tools, AppRoute.Settings };

    public static string PageIdOf(AppRoute route) =>
        Items.First(i => i.Route == route).PageId;

    public static string TitleOf(AppRoute route) =>
        Items.First(i => i.Route == route).Title;
}
