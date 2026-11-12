using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.App.Views;

/// <summary>
/// 软件管家页 code-behind：升级中心经 SoftwarePageViewModel 绑定（三分桶+拦截标死）；
/// 已装/卸载清单经 InstalledListProvider 钩子注入（未接线如实显示「尚未扫描」）。
/// 升级/卸载动作都只是「生成待确认动作」——真正执行必须经用户在确认弹窗里点头，
/// 哈希失配条目标死不可执行（与 VM 同口径，本页不二次放行）。
/// </summary>
public sealed partial class SoftwarePage : Page
{
    /// <summary>已装清单提供者（运行期由宿主接线；未接线=null → 如实显示未扫描）。</summary>
    public static Func<IReadOnlyList<SoftwareEntry>?>? InstalledListProvider { get; set; }

    private SoftwarePageViewModel? _vm;

    public SoftwarePage()
    {
        InitializeComponent();
        ShowTab("store");
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SoftwarePageViewModel vm)
        {
            _vm = vm;
            BindUpgrade(vm);
        }
    }

    /// <summary>注入升级数据（未传时升级中心如实显示未扫描）。</summary>
    public void Bind(SoftwarePageViewModel vm)
    {
        _vm = vm;
        BindUpgrade(vm);
    }

    private void BindUpgrade(SoftwarePageViewModel vm)
    {
        UpgradeSummary.Text = vm.SummaryText;

        if (vm.HasBlocked)
        {
            BlockedHint.Text = $"有 {vm.BlockedByPolicy.Count} 款升级因安装包哈希校验未通过被拦截：不能安装、确认也不放行。";
            BlockedHint.Visibility = Visibility.Visible;
        }
        else
        {
            BlockedHint.Visibility = Visibility.Collapsed;
        }

        // 升级清单：需确认桶在前、静默桶在后（每行一条，动作按钮一律「确认后升级」）
        var rows = vm.NeedConfirm.Select(e => MakeUpgradeRow(e, silent: false))
            .Concat(vm.SilentAllowed.Select(e => MakeUpgradeRow(e, silent: true)))
            .ToList();
        UpgradeList.ItemsSource = rows.Count > 0 ? rows : null;
        if (rows.Count == 0)
            UpgradeSummary.Text = vm.Plan is null ? "尚未扫描软件清单" : "软件都是最新的 · 无拦截";
    }

    private StackPanel MakeUpgradeRow(SoftwareEntry entry, bool silent)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 6, 0, 6) };
        var name = new TextBlock
        {
            Text = entry.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 140,
        };
        var meta = new TextBlock
        {
            Text = $"{SoftwarePageViewModel.SourceText(entry)} · {SoftwarePageViewModel.VersionText(entry)} · {SoftwarePageViewModel.HashBadgeText(entry)}" +
                   (silent ? " · 静默可跑" : string.Empty),
            FontSize = 12.5,
            Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button { Content = "确认后升级", CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 5, 12, 5) };
        btn.Click += async (_, _) => await UpgradeAsync(entry);

        row.Children.Add(name);
        row.Children.Add(meta);
        row.Children.Add(btn);
        return row;
    }

    /// <summary>升级确认弹窗（真机呈现）：点头后才生成动作；这里只编排确认流，不直接触达适配层。</summary>
    private async System.Threading.Tasks.Task UpgradeAsync(SoftwareEntry entry)
    {
        var dialog = new ContentDialog
        {
            Title = $"升级 {entry.Name}",
            Content = $"{SoftwarePageViewModel.SourceText(entry)} · {SoftwarePageViewModel.VersionText(entry)}\n{SoftwarePageViewModel.HashBadgeText(entry)}\n\n确认后由官方源执行升级，装后记入安装账。",
            PrimaryButtonText = "确认升级",
            CloseButtonText = "先不",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        // 动作执行经 CleanupCoordinator 同级的软件编排（宿主接线 UpgradeExecutor 后生效）；
        // 未接线时如实提示，不假装升级成功。
        var resultText = UpgradeExecutor is { } exec ? exec(entry) : "升级通道尚未接线，本次未执行任何动作";
        var done = new ContentDialog
        {
            Title = "升级结果",
            Content = resultText,
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        };
        await done.ShowAsync();
    }

    /// <summary>升级执行器（宿主接线；返回给用户看的结果文案）。</summary>
    public static Func<SoftwareEntry, string>? UpgradeExecutor { get; set; }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) ShowTab(tag);
    }

    private void ShowTab(string tag)
    {
        PaneStore.Visibility = tag == "store" ? Visibility.Visible : Visibility.Collapsed;
        PaneUpgrade.Visibility = tag == "upgrade" ? Visibility.Visible : Visibility.Collapsed;
        PaneUninstall.Visibility = tag == "uninstall" ? Visibility.Visible : Visibility.Collapsed;
        PanePledge.Visibility = tag == "pledge" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (btn, key) in new[] { (TabStore, "store"), (TabUpgrade, "upgrade"), (TabUninstall, "uninstall"), (TabPledge, "pledge") })
            btn.Background = key == tag
                ? (Brush)Application.Current.Resources["TaoAzureWashBrush"]
                : null;

        if (tag == "store") BindStore();
        if (tag == "uninstall") BindUninstall();
    }

    private void BindStore()
    {
        var installed = SafeInstalled();
        if (installed is null)
        {
            StoreHint.Text = "软件清单尚未扫描（扫描后这里显示已装软件与来源）。";
            StoreList.ItemsSource = null;
            return;
        }

        var keyword = StoreSearchBox.Text?.Trim();
        var entries = installed.Where(e => string.IsNullOrEmpty(keyword)
                || e.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || (e.Publisher?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        StoreHint.Text = $"已装 {installed.Count} 款" + (keyword is { Length: > 0 } ? $"，匹配「{keyword}」{entries.Count} 款" : string.Empty);
        StoreList.ItemsSource = entries.Select(e =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 5, 0, 5) };
            row.Children.Add(new TextBlock
            {
                Text = e.Name,
                FontSize = 14,
                MinWidth = 140,
                Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{SoftwarePageViewModel.SourceText(e)} · {SoftwarePageViewModel.VersionText(e)}",
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
            });
            return row;
        }).ToList();
    }

    private void OnStoreSearch(object sender, RoutedEventArgs e) => BindStore();

    private void BindUninstall()
    {
        var installed = SafeInstalled();
        if (installed is null)
        {
            UninstallList.ItemsSource = null;
            return;
        }

        UninstallList.ItemsSource = installed.Select(e =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            row.Children.Add(new TextBlock
            {
                Text = e.Name,
                FontSize = 14,
                MinWidth = 140,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{e.Publisher ?? "未知发行方"} · {e.InstalledVersion ?? "未知版本"}",
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
            });
            var btn = new Button { Content = "确认后卸载", CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 5, 12, 5) };
            btn.Click += async (_, _) => await UninstallAsync(e);
            row.Children.Add(btn);
            return row;
        }).ToList();
    }

    /// <summary>卸载确认弹窗：走系统正规卸载入口（经执行器，未接线时如实提示）。</summary>
    private async System.Threading.Tasks.Task UninstallAsync(SoftwareEntry entry)
    {
        var dialog = new ContentDialog
        {
            Title = $"卸载 {entry.Name}",
            Content = "将调用系统正规卸载入口执行，不做进程强杀式删除。确认后执行。",
            PrimaryButtonText = "确认卸载",
            CloseButtonText = "先不",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var resultText = UninstallExecutor is { } exec ? exec(entry) : "卸载通道尚未接线，本次未执行任何动作";
        var done = new ContentDialog
        {
            Title = "卸载结果",
            Content = resultText,
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        };
        await done.ShowAsync();
    }

    /// <summary>卸载执行器（宿主接线；返回给用户看的结果文案）。</summary>
    public static Func<SoftwareEntry, string>? UninstallExecutor { get; set; }

    private static IReadOnlyList<SoftwareEntry>? SafeInstalled()
    {
        try { return InstalledListProvider?.Invoke(); }
        catch (Exception)
        {
            return null; // 接线异常按未扫描处理，如实显示（不让界面因数据源崩）
        }
    }
}
