using System;
using System.Collections.Generic;
using TaoKeeper.AppKernel.Navigation;

namespace TaoKeeper.App;

/// <summary>
/// 页面注册表：路由 → 已落地页面类型。未注册路由由主窗显示如实占位。
/// 页面逐个落地后在此登记（一级 7 页+二级 5 页全部注册即收口）。
/// </summary>
public static class PageRegistry
{
    private static readonly Dictionary<AppRoute, Type> Pages = new()
    {
        // 已落地页在此登记（XAML + VM 接线完成后；真机核验随完整包环节）。
        // SpaceAnalysis/DuplicateFiles 两页不占独立路由，由工具箱卡片点击 Frame.Navigate 进入；
        // Startup 页只读可导航（经 Boost 页入口直达，不入注册表）。
        [AppRoute.Home]     = typeof(Views.HomePage),
        [AppRoute.Checkup]  = typeof(Views.CheckupPage),
        [AppRoute.Disk]     = typeof(Views.DiskCleanupPage),
        [AppRoute.SwStore]  = typeof(Views.SoftwarePage),
        [AppRoute.Boost]    = typeof(Views.BoostPage),
        [AppRoute.Tools]    = typeof(Views.ToolsPage),
        [AppRoute.Settings] = typeof(Views.SettingsPage),

        // 二级收纳页（G6：经工具箱等入口进入，不入侧栏导航条）
        [AppRoute.Mem]      = typeof(Views.MemPage),
        [AppRoute.Driver]   = typeof(Views.DriverPage),
        [AppRoute.Popup]    = typeof(Views.PopupPage),
        [AppRoute.Storage]  = typeof(Views.StoragePage),
        [AppRoute.Widget]   = typeof(Views.WidgetPage),

        // P-1 深水四页（场景入口路由；不入导航条，SelectRoute 直达，V-A6 侧栏恒 7 项）
        [AppRoute.SandboxPreview] = typeof(Views.SandboxPreviewPage),
        [AppRoute.ResidueDetect]  = typeof(Views.ResidueDetectPage),
        [AppRoute.IncidentReplay] = typeof(Views.IncidentReplayPage),
        [AppRoute.SnapshotChain]  = typeof(Views.SnapshotChainPage),
        [AppRoute.DiskHardcore]   = typeof(Views.DiskHardcorePage),
    };

    /// <summary>路由对应的落地页类型；未落地返回 null（调用方显示占位）。</summary>
    public static Type? PageFor(AppRoute route) =>
        Pages.TryGetValue(route, out var type) ? type : null;
}
