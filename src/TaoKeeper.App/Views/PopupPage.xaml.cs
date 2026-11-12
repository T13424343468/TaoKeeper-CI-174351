using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.Adapters;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.App.Views;

/// <summary>
/// 弹窗管理页 code-behind：规则逐条开关（ToggleSwitch → VM.SetRuleEnabled）、
/// 白名单移出（VM.RemoveWhitelist）、拦截记录回溯（VM.Describe）。
/// 真实闭环（P8-2）：检测=只读枚举可见顶层窗口（合规形态），命中启用规则的窗口经用户确认后
/// WM_CLOSE 优雅关闭并留痕；白名单命中放行留痕。规则/白名单/记录经宿主钩子落盘持久化。
/// 合规形态：本页只维护规则与展示记录，不做任何窗口劫持/注入动作。
/// </summary>
public sealed partial class PopupPage : Page
{
    private PopupPageViewModel? _vm;

    /// <summary>窗口扫描适配器（宿主接线；非 Windows 为 null，检测按钮如实降级）。</summary>
    public static Func<PopupWindowScanner?>? WindowScannerProvider { get; set; }

    /// <summary>检测清单条目：一个真实窗口 + 引擎判定类别（检测时刻快照）。</summary>
    private sealed record DetectRow(DetectedPopupWindow Window, PopupInterceptionRecord Verdict)
    {
        public string Summary { get; } =
            (Verdict.Action switch { PopupAction.Blocked => "命中规则", PopupAction.Whitelisted => "白名单", _ => "未命中" })
            + $" · {Window.ProcessName} · {Window.WindowTitle}";
    }

    public PopupPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is PopupPageViewModel vm)
        {
            _vm = vm;
            Bind(vm);
        }
    }

    /// <summary>注入弹窗规则数据（未传时全空如实展示）。</summary>
    public void Bind(PopupPageViewModel vm)
    {
        _vm = vm;
        Refresh();
    }

    private void Refresh()
    {
        if (_vm is null) return;
        SummaryText.Text = _vm.SummaryText;

        // 规则清单：逐条开关（合规形态，每条独立可控）
        RuleList.ItemsSource = _vm.Rules.Select(rule =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = rule.Title,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
            });
            info.Children.Add(new TextBlock
            {
                Text = $"匹配 {rule.Pattern}",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
            });
            var sw = new ToggleSwitch
            {
                IsOn = rule.Enabled,
                OnContent = "开",
                OffContent = "关",
                Margin = new Thickness(12, 0, 0, 0),
            };
            sw.Toggled += (_, _) =>
            {
                _vm.SetRuleEnabled(rule.Id, sw.IsOn);
                Refresh();
            };
            row.Children.Add(info);
            row.Children.Add(sw);
            return row;
        }).ToList();

        // 白名单
        var wl = _vm.Whitelist;
        WhitelistEmpty.Visibility = wl.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WhitelistList.ItemsSource = wl.Select(entry =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = entry.Note ?? entry.Pattern,
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
            });
            info.Children.Add(new TextBlock
            {
                Text = $"匹配 {entry.Pattern}",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
            });
            var btn = new Button
            {
                Content = "移出",
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 4, 12, 4),
            };
            btn.Click += (_, _) =>
            {
                _vm.RemoveWhitelist(entry.Id);
                Refresh();
            };
            row.Children.Add(info);
            row.Children.Add(btn);
            return row;
        }).ToList();

        // 拦截记录（新→旧，VM 已保证环形保留）
        var records = _vm.RecentRecords;
        RecordsEmpty.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordList.ItemsSource = records.Select(r => (object)new TextBlock
        {
            Text = PopupPageViewModel.Describe(r),
            FontSize = 12.5,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
        }).ToList();
    }

    /// <summary>真实检测：只读枚举可见顶层窗口，逐个交给规则引擎判定（只判不留痕），展示可处置清单。</summary>
    private void OnDetectClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        DetectList.Items.Clear();
        var scanner = WindowScannerProvider?.Invoke();
        if (scanner is null)
        {
            DetectSummary.Text = "当前环境不支持窗口枚举（仅 Windows 真机可用）；未做任何检测，不假数据。";
            return;
        }

        try
        {
            var windows = scanner.GetVisibleTopLevelWindows();
            var rows = new List<DetectRow>();
            foreach (var w in windows)
            {
                var verdict = _vm.Classify(new PopupEvent(w.ProcessName, w.WindowTitle, w.WindowClass));
                rows.Add(new DetectRow(w, verdict));
            }
            foreach (var row in rows)
                DetectList.Items.Add(BuildDetectRow(row, scanner));
            DetectSummary.Text = windows.Count == 0
                ? "未发现可见顶层窗口。"
                : $"发现 {windows.Count} 个可见顶层窗口：命中规则的窗口可屏蔽，白名单放行；未命中窗口不可处置（不越界）。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DetectSummary.Text = $"检测未完成：{ex.Message}（未动任何窗口）";
        }
    }

    private UIElement BuildDetectRow(DetectRow row, PopupWindowScanner scanner)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = row.Summary,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
        });

        // 只有命中启用规则的窗口才给「屏蔽」按钮（确认后 WM_CLOSE）；白名单/未命中窗口不给处置，不越界
        if (row.Verdict.Action == PopupAction.Blocked)
        {
            var block = new Button
            {
                Content = "屏蔽（关闭窗口）",
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 4, 12, 4),
            };
            block.Click += async (_, _) => await ConfirmBlockAsync(row, scanner);
            Grid.SetColumn(block, 1);
            grid.Children.Add(block);
        }
        return grid;
    }

    /// <summary>屏蔽确认弹窗：明示「对哪个窗口、以 WM_CLOSE 优雅关闭、留痕可回溯」，确认后才动手。</summary>
    private async System.Threading.Tasks.Task ConfirmBlockAsync(DetectRow row, PopupWindowScanner scanner)
    {
        if (_vm is null) return;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "确认屏蔽该弹窗？",
                Content = $"对「{row.Window.ProcessName} - {row.Window.WindowTitle}」发送 WM_CLOSE（优雅关闭请求，不是强杀进程），并留痕到拦截记录。",
                PrimaryButtonText = "屏蔽",
                CloseButtonText = "先不动",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var closed = scanner.TryCloseGracefully(row.Window.Handle);
            // 留痕：真实窗口文本 + 用户确认屏蔽；关闭失败也如实记录（动作=已拦截，备注注明未送出）
            _vm.Evaluate(
                new PopupEvent(row.Window.ProcessName, row.Window.WindowTitle, row.Window.WindowClass),
                note: closed ? "用户确认屏蔽（WM_CLOSE 已送出）" : "用户确认屏蔽（WM_CLOSE 送出失败，窗口仍在）");
            Refresh();
        }
        catch (Exception ex)
        {
            DetectSummary.Text = $"屏蔽未完成：{ex.Message}（未动任何窗口）";
        }
    }

    /// <summary>新增一条按进程名匹配的拦截规则（落盘经 VM 钩子）。</summary>
    private void OnAddRuleClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var process = RuleProcessInput.Text.Trim();
        if (process.Length == 0)
        {
            DetectSummary.Text = "请先填进程名（如 popup-push）；空规则不落账。";
            return;
        }
        try
        {
            _vm.AddRule(new PopupRule(
                Id: "rule-" + Guid.NewGuid().ToString("N")[..8],
                Title: $"拦截 {process} 的弹窗",
                MatchKind: PopupMatchKind.ProcessName,
                Pattern: process));
            RuleProcessInput.Text = string.Empty;
            Refresh();
        }
        catch (InvalidOperationException ex)
        {
            DetectSummary.Text = ex.Message; // 规则重复等配置错误如实报红，不静默
        }
    }
}
