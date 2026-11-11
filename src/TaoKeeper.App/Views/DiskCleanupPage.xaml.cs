using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.AppKernel.ViewModels;

namespace TaoKeeper.App.Views;

/// <summary>
/// 磁盘清理页 code-behind：可清项目经 DiskCleanupPageViewModel 绑定（规则级聚合行，勾选即时重算汇总）；
/// 环图弧长 = 已勾选体积 / 可清合计（真实比例，未扫描不出弧不假数）；
/// 「清理」按钮只把已勾选规则转成经确认闸门的待执行清单（弹窗确认后交编排层，P2 接线），本页零写动作。
/// </summary>
public sealed partial class DiskCleanupPage : Page
{
    private DiskCleanupPageViewModel? _vm;

    public DiskCleanupPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is DiskCleanupPageViewModel vm) Bind(vm);
    }

    /// <summary>注入垃圾扫描展示态（未传时保持未扫描空态，不假数据）。</summary>
    public void Bind(DiskCleanupPageViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        Refresh();
    }

    private void Refresh()
    {
        if (_vm is null) return;

        // 环图：未扫描或零候选时不出弧；有数据时弧长=已勾选/合计（真实比例）
        var hasScan = _vm.HasScan;
        RingTotal.Text = _vm.TotalText;
        VolumeText.Text = _vm.VolumeText;
        DrawRing(hasScan && _vm.TotalBytes > 0 ? (double)_vm.SelectedBytes / _vm.TotalBytes : 0);

        // 可清项目行（规则级聚合；勾选态来自 VM：低/中风险默认勾，高风险默认不勾）
        var rows = _vm.Rows;
        JunkEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CleanTotalRow.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        JunkRows.ItemsSource = rows.Select(MakeRow).ToList();
        SelectedSummary.Text = _vm.SelectedSummaryText;
    }

    private UIElement MakeRow(DiskJunkRow row)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new CheckBox
        {
            IsChecked = row.IsChecked,
            IsEnabled = _vm?.HasScan == true,
            MinWidth = 28,
            Tag = row.RuleId,
            VerticalAlignment = VerticalAlignment.Top,
        };
        box.Checked += (_, _) => OnRowToggle(box);
        box.Unchecked += (_, _) => OnRowToggle(box);
        Grid.SetColumn(box, 0);
        grid.Children.Add(box);

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = row.Title,
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
        });
        info.Children.Add(new TextBlock
        {
            Text = row.Description,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var size = new TextBlock
        {
            Text = row.SizeText,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TaoAzureDeepBrush"],
        };
        Grid.SetColumn(size, 2);
        grid.Children.Add(size);
        return grid;
    }

    private void OnRowToggle(CheckBox box)
    {
        if (box.Tag is not string ruleId || _vm is null) return;
        _vm.ToggleRule(ruleId, box.IsChecked);
        Refresh();
    }

    private void DrawRing(double fraction)
    {
        const double stroke = 16, diameter = 176;
        var radius = (diameter - stroke) / 2;
        var circumference = 2 * Math.PI * radius;

        if (fraction is > 0 and <= 1)
        {
            RingArc.Visibility = Visibility.Visible;
            RingArc.StrokeDashArray = new DoubleCollection
            {
                fraction * circumference / stroke, // XAML 虚线单位=描边厚度倍数
                circumference / stroke + 1,
            };
            RingArc.RenderTransform = new RotateTransform { Angle = -90, CenterX = diameter / 2, CenterY = diameter / 2 };
        }
        else
        {
            RingArc.Visibility = Visibility.Collapsed;
        }
    }

    private async System.Threading.Tasks.Task ConfirmCleanupAsync()
    {
        if (_vm is null) return;

        // 零写动作：只把已确认条目整成清单；真正进回收站由 CleanupCoordinator 在编排层执行（P2 接线）
        var actions = _vm.BuildConfirmedActions();
        var refused = _vm.Queue is { } q ? q.Items.Count(i => !i.CanConfirm) : 0;
        var refusalNote = refused > 0 ? $"\n另有 {refused} 条受保护路径条目已由安全闸门标死，不会执行。" : string.Empty;
        var detail = actions.Count == 0
            ? "没有已勾选的可清项目。"
            : string.Join('\n', actions.Take(12).Select(a => $"· {a.FullPath}（{Fmt(a.SizeBytes)}）"))
              + (actions.Count > 12 ? $"\n… 共 {actions.Count} 条" : string.Empty);

        await new ContentDialog
        {
            Title = "清理确认（先进回收站）",
            Content = $"{_vm.SelectedSummaryText}\n\n{detail}{refusalNote}\n\n执行编排接线后，这里将把清单交给清理协调器逐条进回收站（可还原）。当前为展示确认，不做任何删除。",
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }

    private void OnCleanClick(object sender, RoutedEventArgs e) => _ = ConfirmCleanupAsync();

    private static string Fmt(long bytes)
    {
        if (bytes < 0) return "0 B";
        double v = bytes;
        const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
        return bytes >= GB ? $"{v / GB:0.#} GB"
             : bytes >= MB ? $"{v / MB:0.#} MB"
             : bytes >= KB ? $"{v / KB:0.#} KB"
             : $"{bytes} B";
    }
}
