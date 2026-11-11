using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.AppKernel.ViewModels;

namespace TaoKeeper.App.Views;

/// <summary>
/// 重复文件页 code-behind：重复组经 DuplicateFilesPageViewModel 绑定（零误报：全文件 SHA-256 相同才算重复）；
/// 「改为保留」走 VM.TrySetKeep（组内单选互斥，无效选择不动作不报错）；
/// 「转入确认队列」经 CleanupQueueViewModel 产出待执行清单（闸门标死受保护路径），
/// 实际执行由 CleanupCoordinator 接线（P2），本页零写动作。
/// </summary>
public sealed partial class DuplicateFilesPage : Page
{
    private DuplicateFilesPageViewModel? _vm;

    public DuplicateFilesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is DuplicateFilesPageViewModel vm) Bind(vm);
    }

    /// <summary>注入重复文件展示态（未传时保持未扫描空态，不假数据）。</summary>
    public void Bind(DuplicateFilesPageViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        Refresh();
    }

    private void Refresh()
    {
        if (_vm is null) return;

        var groups = _vm.Groups;
        SummaryText.Text = _vm.SummaryText;
        EmptyHint.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupList.ItemsSource = groups.Select(MakeGroupCard).ToList();

        TotalCard.Visibility = groups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TotalText.Text = $"待清理 {_vm.PendingCount} 份，按当前保留选择可释放 {Fmt(_vm.TotalReclaimableBytes)}";
    }

    private UIElement MakeGroupCard(DuplicateFilesPageViewModel.DuplicateGroupItem group)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 12),
            BorderBrush = (Brush)Application.Current.Resources["TaoLineSoftBrush"],
            Background = (Brush)Application.Current.Resources["TaoCardBrush"],
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"重复组 · {Fmt(group.SizeBytes)} × {group.FullPaths.Count} 份",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
        });

        // 保留份（金色徽标 + 路径）
        panel.Children.Add(new TextBlock
        {
            Text = $"保留：{group.KeepPath}",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoGoldBrush"],
        });

        // 待清理份（每行「改为保留」）
        foreach (var path in group.PendingPaths)
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pathText = new TextBlock
            {
                Text = path,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
            };
            Grid.SetColumn(pathText, 0);
            row.Children.Add(pathText);

            var keepButton = new Button
            {
                Content = "改为保留",
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            keepButton.Click += (_, _) => OnKeepClick(group, path);
            Grid.SetColumn(keepButton, 1);
            row.Children.Add(keepButton);
            panel.Children.Add(row);
        }

        panel.Children.Add(new TextBlock
        {
            Text = $"本组可释放 {Fmt(group.ReclaimableBytes)}（待清理 {group.PendingPaths.Count} 份）",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoInk3Brush"],
        });

        card.Content = panel;
        return card;
    }

    private void OnKeepClick(DuplicateFilesPageViewModel.DuplicateGroupItem group, string path)
    {
        if (_vm is null) return;

        // 无效选择（路径不在本组/已是保留份）TrySetKeep 返 false，界面原样不动（与 VM 同口径，不弹错打断）
        group.TrySetKeep(path);
        Refresh();
    }

    private async System.Threading.Tasks.Task ConfirmQueueAsync()
    {
        if (_vm is null) return;

        // 零写动作：只产出待执行清单（受保护路径已由闸门在源头标死）；进回收站由编排层执行（P2 接线）
        var queue = _vm.BuildCleanupQueue();
        queue.SelectAll(); // 待清理份即用户已选定的清理意图，逐条全选可确认项
        var refused = queue.Items.Count(i => !i.CanConfirm);
        var refusalNote = refused > 0 ? $"\n另有 {refused} 条受保护路径条目已被安全闸门标死，不会执行。" : string.Empty;

        await new ContentDialog
        {
            Title = "转入清理确认队列（先进回收站）",
            Content = $"已确认 {queue.ConfirmedCount} 份，合计 {Fmt(queue.ConfirmedBytes)}。{refusalNote}\n\n执行编排接线后，这里将把清单交给清理协调器逐条进回收站（可还原）。当前为展示确认，不做任何删除。",
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }

    private void OnQueueClick(object sender, RoutedEventArgs e) => _ = ConfirmQueueAsync();

    private static string Fmt(long bytes)
    {
        double v = bytes;
        const long GB = 1024L * 1024 * 1024, MB = 1024 * 1024;
        return bytes >= GB ? $"{v / GB:F1} GB"
             : bytes >= MB ? $"{v / MB:F0} MB"
             : $"{bytes} B";
    }
}
