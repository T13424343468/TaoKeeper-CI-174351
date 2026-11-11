using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.AppKernel.Navigation;
using TaoKeeper.AppKernel.ViewModels;

namespace TaoKeeper.App.Views;

/// <summary>
/// 总览页 code-behind：数据经 HomePageViewModel 注入（Bind），未传时按未测态如实展示；
/// 快捷卡点击 → 主窗按 AppRouteCatalog 路由跳转（与发现清单跳转同口径）。
/// </summary>
public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        Bind(HomePageViewModel.Untested());
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 主窗在导航参数里带 VM（HomeViewModelProvider 未接线时为 null → 未测态兜底）
        if (e.Parameter is HomePageViewModel vm) Bind(vm);
    }

    /// <summary>注入总览数据（未测态经 VM 层保证如实，不假 100 分/不假扫描数）。</summary>
    public void Bind(HomePageViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        HeroTitle.Text = vm.HeroTitle;
        HeroSubtitle.Text = vm.HeroSubtitle;

        // 三格统计：未测/未扫显示「—」与如实文案
        StatScore.Text = vm.Score is int s ? s.ToString() : "—";
        StatScoreLabel.Text = $"体检评分 · {vm.ScoreBandText}";
        StatJunk.Text = vm.JunkBytes is long b ? FormatJunkNumber(b) : "—";
        StatUpgrades.Text = vm.UpgradeCount is int u ? u.ToString() : "—";
        StatUpgradesLabel.Text = vm.UpgradeCount is int uc
            ? (vm.BlockedCount > 0 ? $"软件可升级 · {vm.BlockedCount} 项已拦截" : "软件可升级 · 无拦截")
            : vm.UpgradeSummaryText;

        // 快捷卡（标题/摘要/路由）
        for (var i = 0; i < 4; i++)
        {
            var (title, sub) = i < vm.QuickCards.Count
                ? (vm.QuickCards[i].Title, vm.QuickCards[i].Subtitle)
                : ("—", string.Empty);
            switch (i)
            {
                case 0: Quick0Title.Text = title; Quick0Sub.Text = sub; break;
                case 1: Quick1Title.Text = title; Quick1Sub.Text = sub; break;
                case 2: Quick2Title.Text = title; Quick2Sub.Text = sub; break;
                case 3: Quick3Title.Text = title; Quick3Sub.Text = sub; break;
            }
        }

        // 清理账：无账显示空态；有账只显示最近三笔（VM 已按原型倒序）
        var lines = vm.LedgerLines;
        LedgerEmpty.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LedgerLine0.Text = lines.Count > 0 ? lines[0].SummaryText : string.Empty;
        LedgerLine1.Text = lines.Count > 1 ? lines[1].SummaryText : string.Empty;
        LedgerLine2.Text = lines.Count > 2 ? lines[2].SummaryText : string.Empty;
    }

    private void OnQuickCardClick(object sender, RoutedEventArgs e)
    {
        var shell = (App.Current as App)?.Shell;
        if (shell is null) return;

        var route = sender switch
        {
            _ when ReferenceEquals(sender, Quick0) => AppRoute.SwStore,
            _ when ReferenceEquals(sender, Quick1) => AppRoute.Disk,
            _ when ReferenceEquals(sender, Quick2) => AppRoute.Mem,
            _ when ReferenceEquals(sender, Quick3) => AppRoute.Boost,
            _ => AppRoute.Home,
        };
        shell.SelectRoute(route);
    }

    private static string FormatJunkNumber(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#}",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#}",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#}",
    };
}
