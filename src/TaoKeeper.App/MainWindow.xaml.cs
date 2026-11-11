using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.App.Theming;
using TaoKeeper.AppKernel.Navigation;

namespace TaoKeeper.App;

/// <summary>侧栏导航条目（glyph 单字 + 页名，取自原型 v19 导航栏）。</summary>
public sealed class NavItem
{
    public string Glyph { get; }
    public string Title { get; }
    public AppRoute Route { get; }

    public NavItem(AppRoute route)
    {
        Route = route;
        Title = AppRouteCatalog.TitleOf(route);
        // 原型导航 glyph：单字方形章（总览=览、软件管家=管…），与 AppRouteCatalog 顺序一一对应
        Glyph = route switch
        {
            AppRoute.Home => "览",
            AppRoute.SwStore => "管",
            AppRoute.Disk => "盘",
            AppRoute.Checkup => "诊",
            AppRoute.Boost => "疾",
            AppRoute.Mem => "存",
            AppRoute.Driver => "驱",
            AppRoute.Popup => "静",
            AppRoute.Storage => "储",
            AppRoute.Tools => "匣",
            AppRoute.Widget => "悬",
            AppRoute.Settings => "设",
            AppRoute.Design => "记",
            _ => "页",
        };
    }
}

/// <summary>
/// 主窗骨架：左导航 + 内容 Frame。导航条目严格按原型 v19 顺序（交付版不出现「设计说明」）。
/// 页面注册表：已落地页直接导航；未落地页显示如实占位（不假数据、不空转）。
/// </summary>
public sealed partial class MainWindow : Window
{
    private IReadOnlyList<NavItem> _navItems;
    private bool _suppressNavEvent;

    public MainWindow()
    {
        InitializeComponent();
        // 标题栏带显式版本号（与 csproj/Product.wxs 交付版号 1.0.0.0 同口径，映射源码戳 PC-FULL-V1）
        Title = "道管家 · TaoKeeper v1.0.0.0";

        _navItems = AppRouteCatalog.NavBarRoutes.Select(r => new NavItem(r)).ToList();
        NavList.ItemsSource = _navItems;
        NavList.SelectedIndex = 0; // 首启=总览（原型首页）

        Activate();
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavEvent) return;
        if (NavList.SelectedItem is not NavItem item) return;
        Navigate(item.Route);
    }

    /// <summary>按路由切页：已落地页直达（VM Provider 有接线则带数据参数）；未落地页显示占位（占位不假数据，如实说明）。</summary>
    public void Navigate(AppRoute route)
    {
        var pageType = PageRegistry.PageFor(route);
        if (pageType is not null)
        {
            // 导航参数带数据 VM（各页 Provider 未接线时为 null，页面按未测态如实兜底）
            ContentFrame.Navigate(pageType, ParameterFor(route));
            return;
        }

        ContentFrame.Navigate(typeof(PlaceholderPage), AppRouteCatalog.TitleOf(route));
    }

    /// <summary>按路由取导航参数（VM Provider 未接线返回 null，页面按未测态兜底）。</summary>
    private object? ParameterFor(AppRoute route) => route switch
    {
        AppRoute.Home => HomeViewModelProvider?.Invoke(),
        AppRoute.Boost => BoostViewModelProvider?.Invoke(),
        AppRoute.Mem => MemViewModelProvider?.Invoke(),
        AppRoute.SwStore => SwStoreViewModelProvider?.Invoke(),
        AppRoute.Settings => SettingsViewModelProvider?.Invoke(),
        AppRoute.Disk => DiskViewModelProvider?.Invoke(),
        AppRoute.Driver => DriverViewModelProvider?.Invoke(),
        _ => null,
    };

    /// <summary>总览数据提供者（应用运行期接线；未接线时总览页显示未测态，不假数据）。</summary>
    public Func<TaoKeeper.AppKernel.ViewModels.HomePageViewModel>? HomeViewModelProvider { get; set; }

    /// <summary>性能加速数据提供者（运行期接线；未接线时加速页显示未测态）。</summary>
    public Func<TaoKeeper.AppKernel.ViewModels.SpeedupPageViewModel>? BoostViewModelProvider { get; set; }

    /// <summary>内存管理数据提供者（运行期接线；未接线时内存页显示空态）。</summary>
    public Func<TaoKeeper.AppKernel.ViewModels.MemPageViewModel>? MemViewModelProvider { get; set; }

    /// <summary>软件管家数据提供者（运行期接线；未接线时软件管家页显示未扫描态）。</summary>
    public Func<TaoKeeper.AppKernel.ViewModels.SoftwarePageViewModel>? SwStoreViewModelProvider { get; set; }

    /// <summary>设置数据提供者（运行期接线；未接线时设置页按默认态兜底）。</summary>
    public Func<TaoKeeper.AppKernel.ViewModels.SettingsPageViewModel>? SettingsViewModelProvider { get; set; }

    /// <summary>磁盘清理数据提供者（运行期接线；未接线时磁盘页显示未扫描空态）。</summary>
    public Func<TaoKeeper.AppKernel.ViewModels.DiskCleanupPageViewModel>? DiskViewModelProvider { get; set; }

    /// <summary>驱动升级数据提供者（运行期接线；未接线时驱动页显示未扫描态）。</summary>
    public Func<TaoKeeper.AppKernel.ViewModels.DriverPageViewModel>? DriverViewModelProvider { get; set; }

    /// <summary>程序化选中导航项（页面间互跳用）；design 路由不在交付导航条内。</summary>
    public void SelectRoute(AppRoute route)
    {
        var index = -1;
        for (var i = 0; i < _navItems.Count; i++)
        {
            if (_navItems[i].Route != route) continue;
            index = i;
            break;
        }
        if (index < 0) return; // 不在导航条内的路由只切内容，不动选中态
        _suppressNavEvent = true;
        NavList.SelectedIndex = index;
        _suppressNavEvent = false;
        Navigate(route);
    }

    /// <summary>运行期换主题（设置页调用）；全局即时生效，与 AppTheme 同一实现。</summary>
    public void ApplyTheme(TaoKeeper.AppKernel.Theming.ThemeName theme) =>
        AppTheme.Apply(Application.Current, theme);
}
