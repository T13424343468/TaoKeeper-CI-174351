using System;
using System.Collections.Generic;
using TaoKeeper.AppKernel.Navigation;

namespace TaoKeeper.App;

/// <summary>
/// 页面注册表：路由 → 已落地页面类型。未注册路由由主窗显示如实占位
/// （「本页正在赶工」，不假数据不空转）。页面逐个落地后在此登记（13 页全部注册即收口）。
/// </summary>
public static class PageRegistry
{
    private static readonly Dictionary<AppRoute, Type> Pages = new()
    {
        // 已落地页在此登记（XAML + VM 接线完成后；真机核验随完整包环节）。
        // SpaceAnalysis/DuplicateFiles 两页不占独立路由，由工具箱卡片点击 Frame.Navigate 进入；
        // Startup 页只读可导航（经 Boost 页入口直达，不入注册表）。
        [AppRoute.Home]     = typeof(Views.HomePage),
        [AppRoute.SwStore]  = typeof(Views.SoftwarePage),
        [AppRoute.Disk]     = typeof(Views.DiskCleanupPage),
        [AppRoute.Checkup]  = typeof(Views.CheckupPage),
        [AppRoute.Boost]    = typeof(Views.BoostPage),
        [AppRoute.Mem]      = typeof(Views.MemPage),
        [AppRoute.Driver]   = typeof(Views.DriverPage),
        [AppRoute.Popup]    = typeof(Views.PopupPage),
        [AppRoute.Storage]  = typeof(Views.StoragePage),
        [AppRoute.Tools]    = typeof(Views.ToolsPage),
        [AppRoute.Widget]   = typeof(Views.WidgetPage),
        [AppRoute.Settings] = typeof(Views.SettingsPage),

        // P-1 深水四页（场景入口路由；不入导航条，SelectRoute 直达，V-A6 导航条不动）
        [AppRoute.SandboxPreview] = typeof(Views.SandboxPreviewPage),
        [AppRoute.ResidueDetect]  = typeof(Views.ResidueDetectPage),
        [AppRoute.IncidentReplay] = typeof(Views.IncidentReplayPage),
        [AppRoute.SnapshotChain]  = typeof(Views.SnapshotChainPage),
    };

    /// <summary>路由对应的落地页类型；未落地返回 null（调用方显示占位）。</summary>
    public static Type? PageFor(AppRoute route) =>
        Pages.TryGetValue(route, out var type) ? type : null;
}
