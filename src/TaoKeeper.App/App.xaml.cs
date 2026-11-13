using System;
using Microsoft.UI.Xaml;
using TaoKeeper.App.Theming;
using TaoKeeper.AppKernel.Navigation;

namespace TaoKeeper.App;

/// <summary>
/// 应用入口：读用户设置的三主题（水墨/天青/墨白）换入对应资源字典，再起主窗。
/// 主题色值与 AppKernel.Theming.ThemeTokens 同源（原型 v19），双处只改一处须同步。
/// </summary>
public partial class App : Application
{
    private Window? _mainWindow;

    /// <summary>主窗引用（页面经此跳转/换肤；未启动完成前为 null）。</summary>
    internal MainWindow? Shell => _mainWindow as MainWindow;

    /// <summary>托盘「打开道管家」/左键唤回：整窗显示并前置（与 MainWindow.ShowFromTray 同一入口）。</summary>
    private void ShowShell() => Shell?.ShowFromTray();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppTheme.Apply(this, AppTheme.LoadThemeFromSettings());

        _mainWindow = new MainWindow();
        // 组合根接线（各页数据提供者接真实引擎/适配器；接线唯一入口，见 CompositionRoot）
        CompositionRoot.Wire((MainWindow)_mainWindow);

        // R-P12-9 托盘：图标常驻（左键开窗 / 右键五项目菜单）；唤回与挂件路由在此接线，清理与自启在组合根接
        TrayIconController.OpenMainWindowHandler = ShowShell;
        TrayIconController.ShowWidgetHandler = () => Shell?.SelectRoute(AppRoute.Widget);
        TrayIconController.Initialize();

        _mainWindow.Activate();

        // 直达路由（实效探针/排障用）：--page <pageId|标题>；未识别保持首页，现行默认行为不变
        if (LaunchRoute.FromCommandLine(Environment.GetCommandLineArgs()) is { } direct)
        {
            Shell?.NavigateDirect(direct);
        }

        GC.KeepAlive(typeof(AppRouteCatalog)); // 导航目录随壳常驻（保持引用，防裁剪）
    }
}
