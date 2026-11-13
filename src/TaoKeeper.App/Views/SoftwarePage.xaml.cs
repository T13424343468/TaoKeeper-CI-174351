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
            // G7：不出现哈希术语，说人话；拦截=安全校验未过，确认也不放行（与策略硬规则一致）
            BlockedHint.Text = $"有 {vm.BlockedByPolicy.Count} 款升级没能通过安全校验，已被拦下：不能安装、确认也不放行。";
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
            // G7 来源收后台：行内不出现来源渠道名与哈希术语，只留版本与用户可懂的安全状态
            Text = $"{SoftwarePageViewModel.SafetyText(entry)} · {SoftwarePageViewModel.VersionText(entry)}" +
                   (silent ? " · 可自动升级" : string.Empty),
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
            Content = $"{SoftwarePageViewModel.VersionText(entry)}\n{SoftwarePageViewModel.SafetyText(entry)}\n\n确认后由官方渠道执行升级，装后记入安装账。",
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

        // R-3 收口：检索零命中给明确空态行（R-P10-8 空态口径），不留白板
        if (keyword is { Length: > 0 } && entries.Count == 0)
        {
            StoreList.ItemsSource = new object[]
            {
                new TextBlock
                {
                    Text = "没有匹配的软件：换个关键词，或清空检索看全部已装清单。",
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TaoInk3Brush"],
                },
            };
            return;
        }

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
                // G7 来源收后台：已装行只留版本，不外显来源渠道
                Text = SoftwarePageViewModel.VersionText(e),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
            });
            return row;
        }).ToList();
    }

    private async void OnStoreSearch(object sender, RoutedEventArgs e)
    {
        var keyword = StoreSearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            BindStore(); // 空关键词回已装清单视图
            return;
        }

        // R-P2-10 检索真实通道：经宿主接线的 SearchExecutor（winget search）；转后台跑，按钮即回显检索中
        if (StoreSearchExecutor is null)
        {
            StoreHint.Text = "软件库检索通道尚未接线；已装软件可用下方清单，或稍后再试。";
            StoreList.ItemsSource = null;
            return;
        }

        StoreHint.Text = $"正在检索「{keyword}」…可先看其他页签，检索完自动展示。";
        StoreList.ItemsSource = null;
        try
        {
            var results = await StoreSearchExecutor(keyword);
            StoreHint.Text = results.Count > 0
                ? $"「{keyword}」找到 {results.Count} 款可安装软件："
                : $"「{keyword}」没有找到结果——换个说法再试试，或看看已装软件清单。";
            StoreList.ItemsSource = results.Count > 0
                ? results.Select(e =>
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
                        // G7：来源不外显；检索候选只留版本（未标版本如实显「待装时定」）
                        Text = e.AvailableVersion ?? "版本以安装时为准",
                        FontSize = 12.5,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
                    });
                    return row;
                }).ToList()
                : null;
        }
        catch (Exception)
        {
            // 失败降级如实报（不假空、不吞错）：检索通道暂不可用，留引导不装死
            StoreHint.Text = "检索通道这会儿不通（可能网络或服务暂不可用）；已装软件清单仍可正常用。";
        }
    }

    /// <summary>软件库检索执行器（宿主接线 winget search；返回可安装候选清单）。</summary>
    public static Func<string, System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<SoftwareEntry>>>? StoreSearchExecutor { get; set; }

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
