using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules.SnapshotChain;

namespace TaoKeeper.App.Views;

/// <summary>
/// P16 时光回溯 code-behind：配额管理器/引擎由组合根接线。
/// 还原走确认流（确认→进度→完成/失败，A16-2）；到期粉碎快照清除前二次确认（R-P16-1）。
/// </summary>
public sealed partial class SnapshotChainPage : Page
{
    private SnapshotChainPageViewModel? _vm;

    /// <summary>配额管理器提供者（组合根接线；未接线时页面如实提示不可用）。</summary>
    public static Func<SnapshotQuotaTracker>? TrackerProvider { get; set; }

    /// <summary>还原执行器（组合根接线：引擎 RestoreAsync，WAL 二态收敛）。</summary>
    public static Func<string, System.Threading.Tasks.Task<RestoreResult>>? RestoreExecutor { get; set; }

    /// <summary>淘汰执行器（组合根接线：滚动淘汰到配额，物理清除前页面二次确认）。</summary>
    public static Action<string>? EvictSnapshot { get; set; }

    public SnapshotChainPage() => InitializeComponent();

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (TrackerProvider is null)
        {
            ResultLine.Text = "快照引擎未就绪（等待运行期接线），不假数据。";
            return;
        }

        _vm = new SnapshotChainPageViewModel(TrackerProvider());
        _vm.Refresh();
        Render();
        ResultLine.Text = $"清单已刷新：共 {_vm.Snapshots.Count} 笔快照。";
    }

    private async void OnRestoreLatest(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.Snapshots.Count == 0)
        {
            ResultLine.Text = _vm is null ? "请先刷新清单。" : "还没有可还原的快照。";
            return;
        }

        // 还原确认流（A16-2 原型 flowRestore2：确认→进度→完成/失败全状态流）
        var dialog = new ContentDialog
        {
            Title = "还原最近一次快照",
            Content = $"将把系统状态还原到最近一次快照（{_vm.Snapshots[0].TimeLocal}）。继续吗？",
            PrimaryButtonText = "还原",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        RunRing.Visibility = Visibility.Visible;
        try
        {
            var result = RestoreExecutor is not null
                ? await RestoreExecutor(_vm.Snapshots[0].TransactionId).ConfigureAwait(true)
                : RestoreResult.Failed("还原执行器未接线");
            ResultLine.Text = result.Succeeded
                ? "还原完成。"
                : $"还原失败：{result.FailureReason}（未决记录保留，等待下次恢复，不静默丢弃）";
        }
        finally
        {
            RunRing.Visibility = Visibility.Collapsed;
        }
    }

    private void Render()
    {
        if (_vm is null) return;
        UsageLine.Text = $"总占用：{_vm.TotalUsageText}";
        QuotaLine.Text = _vm.QuotaText;
        EmptyLine.Visibility = _vm.Snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SnapshotList.Items.Clear();
        foreach (var row in _vm.Snapshots)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Text = $"{row.TimeLocal}｜{row.ActionLabel}｜{row.ObjectCount} 项｜{row.SizeLabel}",
            });
            var restore = new Button
            {
                Content = "还原",
                FontSize = 11,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 4, 12, 4),
            };
            var id = row.TransactionId;
            restore.Click += async (_, _) =>
            {
                RunRing.Visibility = Visibility.Visible;
                try
                {
                    var result = RestoreExecutor is not null
                        ? await RestoreExecutor(id).ConfigureAwait(true)
                        : RestoreResult.Failed("还原执行器未接线");
                    ResultLine.Text = result.Succeeded ? "还原完成。" : $"还原失败：{result.FailureReason}";
                }
                finally
                {
                    RunRing.Visibility = Visibility.Collapsed;
                }
            };
            panel.Children.Add(restore);
            SnapshotList.Items.Add(panel);
        }
    }
}
