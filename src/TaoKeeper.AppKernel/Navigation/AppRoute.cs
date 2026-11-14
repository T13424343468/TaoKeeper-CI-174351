namespace TaoKeeper.AppKernel.Navigation;

/// <summary>
/// 导航路由：严格对齐原型 v19 左侧导航顺序（13 页）。
/// 顺序不可改动；原型为唯一 UI 基线。
/// </summary>
public enum AppRoute
{
    Home = 0,     // 总览
    SwStore,      // 软件管家
    Disk,         // 磁盘清理
    Checkup,      // 系统体检
    Boost,        // 性能加速
    Mem,          // 内存管理
    Driver,       // 驱动安装升级
    Popup,        // 弹窗管理
    Storage,      // 存储感知
    Tools,        // 工具箱
    Widget,       // 桌面挂件
    Settings,     // 设置
    Design,       // 设计说明（开发期保留，交付版隐藏）

    // —— 场景入口路由（P-1 深水四页；不入导航条，经场景入口 SelectRoute 跳转，V-A6 侧栏恒 7 项/12 项导航条不动）——
    SandboxPreview,   // P13 先试后行（沙箱预演）
    ResidueDetect,    // P14 残留侦探（引用图）
    IncidentReplay,   // P15 事发回放（归因报告）
    SnapshotChain,    // P16 时光回溯（快照链）
}

public static class AppRouteCatalog
{
    /// <summary>导航顺序与原型 data-page 一一对应；页面 id 与中文名严格取自原型 v19。</summary>
    public static readonly (AppRoute Route, string PageId, string Title)[] Items =
    {
        (AppRoute.Home,    "home",    "总览"),
        (AppRoute.SwStore, "swstore", "软件管家"),
        (AppRoute.Disk,    "disk",    "磁盘清理"),
        (AppRoute.Checkup, "checkup", "系统体检"),
        (AppRoute.Boost,   "boost",   "性能加速"),
        (AppRoute.Mem,     "mem",     "内存管理"),
        (AppRoute.Driver,  "driver",  "驱动安装升级"),
        (AppRoute.Popup,   "popup",   "弹窗管理"),
        (AppRoute.Storage, "storage", "存储感知"),
        (AppRoute.Tools,   "tools",   "工具箱"),
        (AppRoute.Widget,  "widget",  "桌面挂件"),
        (AppRoute.Settings,"settings","设置"),
        (AppRoute.Design,  "design",  "设计说明"),
    };

    /// <summary>交付版导航条不出现「设计说明」（开发期入口，见原型页脚注释）。</summary>
    public static readonly AppRoute[] NavBarRoutes =
        Items.Where(i => i.Route != AppRoute.Design).Select(i => i.Route).ToArray();

    public static string PageIdOf(AppRoute route) =>
        Items.First(i => i.Route == route).PageId;

    public static string TitleOf(AppRoute route) =>
        Items.First(i => i.Route == route).Title;
}
