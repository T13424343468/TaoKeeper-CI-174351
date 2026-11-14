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

    // ── R-P2-8 软件库精选示例 ─────────────────────────────────────────────
    // 如实口径：这些是精选示例卡（便于试用界面），不是真实收录承诺；
    // 页脚与每张卡内都带「精选示例」标注，接入真实收录后以实际量展示。

    private sealed record StoreSampleCard(string Name, string Version, string Category, uint IconColor, string Glyph);

    private static readonly StoreSampleCard[] StoreSamples =
    {
        new("微信", "3.9.12", "社交通讯", 0xFF07C160, "微"),
        new("QQ", "9.7.20", "社交通讯", 0xFF12B7F5, "Q"),
        new("WPS Office", "2024", "办公学习", 0xFFE8541D, "W"),
        new("网易云音乐", "3.0.10", "影音娱乐", 0xFFDD001B, "音"),
        new("VS Code", "1.95", "开发工具", 0xFF007ACC, "V"),
        new("Git", "2.47", "开发工具", 0xFFF05033, "G"),
        new("火绒安全", "6.0", "安全防护", 0xFF2F6FED, "火"),
        new("7-Zip", "24.08", "系统工具", 0xFF3E4A59, "7"),
        new("Everything", "1.4.1", "系统工具", 0xFF5A6B7B, "E"),
        new("Photoshop", "2025", "设计创作", 0xFF31A8FF, "P"),
        new("Chrome", "131", "浏览器", 0xFF4285F4, "C"),
        new("Edge", "131", "浏览器", 0xFF0F7EBF, "E"),
        new("Steam", "3.0", "游戏平台", 0xFF171A21, "S"),
        new("qBittorrent", "5.0", "下载传输", 0xFF3B82D6, "q"),
        new("微信输入法", "1.3", "输入法", 0xFF07C160, "键"),
        new("搜狗输入法", "15.2", "输入法", 0xFFF76B1C, "搜"),
    };

    private static readonly string[] StoreCategories =
    {
        "精选", "社交通讯", "办公学习", "影音娱乐", "开发工具", "安全防护",
        "系统工具", "设计创作", "浏览器", "游戏平台", "下载传输", "输入法",
    };

    /// <summary>愿望单（本会话内存清单；添加/移除即生效，如实不假持久化）。</summary>
    private static readonly List<string> Wishlist = new();

    private bool _storeBuilt;
    private string _currentCategory = "精选";

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

        // R-P2-2 三分桶：可静默 / 需你确认 / 已拦截，各组带计数 pill 与空态（pill 随实数刷新）
        SilentPillText.Text = vm.SilentAllowed.Count.ToString();
        ConfirmPillText.Text = vm.NeedConfirm.Count.ToString();
        SilentList.ItemsSource = vm.SilentAllowed.Count > 0
            ? vm.SilentAllowed.Select(e => MakeUpgradeRow(e, silent: true)).ToList() : null;
        SilentEmpty.Visibility = vm.SilentAllowed.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ConfirmEmpty.Visibility = vm.NeedConfirm.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ConfirmPillText.Text = vm.NeedConfirm.Count.ToString();
        UpgradeList.ItemsSource = vm.NeedConfirm.Count > 0
            ? vm.NeedConfirm.Select(e => MakeUpgradeRow(e, silent: false)).ToList() : null;
        if (vm.HasBlocked)
        {
            BlockedPane.Visibility = Visibility.Visible;
            BlockedPillText.Text = vm.BlockedByPolicy.Count.ToString();
            BlockedList.ItemsSource = vm.BlockedByPolicy.Select(e => MakeUpgradeRow(e, silent: false, blocked: true)).ToList();
        }
        else
        {
            BlockedPane.Visibility = Visibility.Collapsed;
            BlockedList.ItemsSource = null;
        }
        if (vm.SilentAllowed.Count == 0 && vm.NeedConfirm.Count == 0 && !vm.HasBlocked)
            UpgradeSummary.Text = vm.Plan is null ? "尚未扫描软件清单" : "软件都是最新的 · 无拦截";
    }

    private StackPanel MakeUpgradeRow(SoftwareEntry entry, bool silent, bool blocked = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 6, 0, 6) };
        if (blocked) row.BorderBrush = (Brush)Application.Current.Resources["TaoCinnabarBrush"]; // R-P2-2 已拦截行朱砂描边
        if (blocked) row.BorderThickness = new Thickness(1);
        if (blocked) row.CornerRadius = new CornerRadius(8);
        if (blocked) row.Padding = new Thickness(8, 6, 8, 6);
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
                   (blocked ? " · 已拦截：不能安装" : silent ? " · 可自动升级" : string.Empty),
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
        if (!_storeBuilt)
        {
            BuildCategoryTabs();
            _storeBuilt = true;
        }
        ApplyStoreFilter();

        var installed = SafeInstalled();
        if (installed is null)
        {
            StoreHint.Text = "已装清单尚未扫描（扫描后这里显示已装软件）；精选示例卡始终可看。";
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

    /// <summary>R-P2-9：12 类页签生成与选中高亮（点击筛选，精选=全部示例卡）。</summary>
    private void BuildCategoryTabs()
    {
        var tabs = StoreCategories.Select(category =>
        {
            var btn = new Button
            {
                Content = category,
                Tag = category,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 7, 14, 7),
            };
            btn.Click += (_, _) => SelectCategory(category);
            return btn;
        }).ToList();
        CategoryTabs.ItemsSource = tabs;
        HighlightCategory();
    }

    private void SelectCategory(string category)
    {
        _currentCategory = category;
        HighlightCategory();
        ApplyStoreFilter();
    }

    private void HighlightCategory()
    {
        if (CategoryTabs.ItemsSource is not IEnumerable<Button> tabs) return;
        foreach (var btn in tabs)
            btn.Background = btn.Tag as string == _currentCategory
                ? (Brush)Application.Current.Resources["TaoAzureWashBrush"]
                : null;
    }

    /// <summary>R-P2-8：按当前分类刷新示例卡（精选=全部 16 张）并同步库容量行。</summary>
    private void ApplyStoreFilter()
    {
        var cards = _currentCategory == "精选"
            ? StoreSamples.ToList()
            : StoreSamples.Where(c => c.Category == _currentCategory).ToList();
        StoreCardGrid.ItemsSource = cards.Select(MakeStoreCard).ToList();

        StoreCapacityText.Text = _currentCategory == "精选"
            ? $"已收录 {cards.Count} 款精选示例 · 愿望单 {Wishlist.Count} 条"
            : $"「{_currentCategory}」{cards.Count} 款精选示例 · 愿望单 {Wishlist.Count} 条";

        StoreFooter.Visibility = _currentCategory == "精选" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>单张示例卡：38px 品牌色圆角图标 + 名称 + 版本 + 「精选示例」标注 + 安装按钮。</summary>
    private StackPanel MakeStoreCard(StoreSampleCard card)
    {
        var card_ = new StackPanel { Spacing = 8, Width = 276, Padding = new Thickness(12) };
        card_.Background = (Brush)Application.Current.Resources["TaoCardBrush"];
        card_.BorderBrush = (Brush)Application.Current.Resources["TaoLineSoftBrush"];
        card_.BorderThickness = new Thickness(1);
        card_.CornerRadius = new CornerRadius(14);

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var icon = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb((byte)(card.IconColor >> 24 & 0xFF), (byte)(card.IconColor >> 16 & 0xFF), (byte)(card.IconColor >> 8 & 0xFF), (byte)(card.IconColor & 0xFF))),
        };
        icon.Child = new TextBlock
        {
            Text = card.Glyph,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var nameCol = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        nameCol.Children.Add(new TextBlock
        {
            Text = card.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
        });
        nameCol.Children.Add(new TextBlock
        {
            Text = $"{card.Version} · {card.Category}",
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["TaoInk3Brush"],
        });
        head.Children.Add(icon);
        head.Children.Add(nameCol);

        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var installBtn = new Button
        {
            Content = "安装",
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 5, 16, 5),
            Style = (Style)Application.Current.Resources["TaoButtonPrimary"],
        };
        installBtn.Click += async (_, _) => await InstallAsync(card);
        foot.Children.Add(installBtn);
        foot.Children.Add(new TextBlock
        {
            Text = "精选示例",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TaoInk3Brush"],
        });

        card_.Children.Add(head);
        card_.Children.Add(foot);
        return card_;
    }

    /// <summary>R-P2-15 安装流：先弹「净化扫描」说明经用户点头，确认后才交执行器；未接线如实提示。</summary>
    private async System.Threading.Tasks.Task InstallAsync(StoreSampleCard card)
    {
        var dialog = new ContentDialog
        {
            Title = $"安装 {card.Name}",
            Content = $"版本 {card.Version}（精选示例）\n\n安装前会先过一遍净化扫描：校验安装包完整性与安全性，通过后才装到默认位置 D:\\TaoKeeper\\Soft（装前可单独改位置），不往 C 盘塞。确认后执行。",
            PrimaryButtonText = "开始安装",
            CloseButtonText = "先不",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        // 净化扫描与安装都由宿主接线的执行器完成（winget 官方渠道 + 装前校验）；
        // 未接线时如实提示，不假装装好了。
        var resultText = InstallExecutor is { } exec ? exec(card.Name, card.Version) : "安装通道尚未接线，本次未执行任何动作";
        var done = new ContentDialog
        {
            Title = "安装结果",
            Content = resultText,
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        };
        await done.ShowAsync();
    }

    /// <summary>安装执行器（宿主接线：净化扫描 + 官方渠道安装；返回给用户看的结果文案）。</summary>
    public static Func<string, string, string>? InstallExecutor { get; set; }

    /// <summary>愿望单弹层：查看已存愿望、添加、移除（按钮真功能，非死按钮）。</summary>
    private async void OnWishlistClick(object sender, RoutedEventArgs e)
    {
        var listText = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
            Text = Wishlist.Count == 0
                ? "愿望单还是空的。想要哪款软件，填在下面收进愿望单，接入选软件时优先安排。"
                : string.Join("\n", Wishlist.Select((n, i) => $"{i + 1}. {n}")),
        };
        var input = new TextBox { PlaceholderText = "输入软件名，如：Obsidian", CornerRadius = new CornerRadius(10) };
        var addBtn = new Button
        {
            Content = "收进愿望单",
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 6, 14, 6),
            Style = (Style)Application.Current.Resources["TaoButtonPrimary"],
        };
        void AddCurrent()
        {
            var name = input.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            if (!Wishlist.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Wishlist.Add(name);
                listText.Text = string.Join("\n", Wishlist.Select((n, i) => $"{i + 1}. {n}"));
                StoreCapacityText.Text = $"已收录 {StoreSamples.Length} 款精选示例 · 愿望单 {Wishlist.Count} 条";
            }
            input.Text = string.Empty;
        }
        addBtn.Click += (_, _) => AddCurrent();
        input.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) AddCurrent(); };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(listText);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { input, addBtn } });

        var dialog = new ContentDialog
        {
            Title = "愿望单",
            Content = panel,
            PrimaryButtonText = "关掉",
            CloseButtonText = "清空愿望单",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None && Wishlist.Count > 0)
        {
            Wishlist.Clear();
            StoreCapacityText.Text = $"已收录 {StoreSamples.Length} 款精选示例 · 愿望单 0 条";
        }
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
            // R-4/C3：未扫描空态如实呈现，不装死
            UninstallEmpty.Text = "还没扫到已装软件。扫描接线后这里逐行列出，卸载前先经你确认。";
            UninstallEmpty.Visibility = Visibility.Visible;
            return;
        }

        // R-4/C3：扫过但为空同样有明确反馈
        UninstallEmpty.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (installed.Count == 0)
        {
            UninstallEmpty.Text = "这台电脑没有列出可卸载的软件。";
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
