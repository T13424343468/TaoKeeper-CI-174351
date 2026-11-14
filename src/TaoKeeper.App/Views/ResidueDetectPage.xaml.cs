using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules.ResidueDetect;

namespace TaoKeeper.App.Views;

/// <summary>
/// P14 残留侦探 code-behind：节点源由组合根接线（真实注册表/文件枚举，只读）。
/// 候选/待确认分离展示；待确认保留项不再提示；清理走既有执行链（回收站语义+快照链事务）。
/// </summary>
public sealed partial class ResidueDetectPage : Page
{
    private readonly ResidueDetectPageViewModel _vm = new();

    /// <summary>节点源提供者（组合根接线；未接线时如实提示不可用）。</summary>
    public static Func<(IResidueNodeSource Source, string[] InstallRoots)>? SourceProvider { get; set; }

    /// <summary>清理执行器（组合根接线：CleanupCoordinator 回收站语义 + 快照链事务先行）。</summary>
    public static Func<string[], System.Threading.Tasks.Task>? CleanupExecutor { get; set; }

    public ResidueDetectPage() => InitializeComponent();

    private void OnScan(object sender, RoutedEventArgs e)
    {
        if (SourceProvider is null)
        {
            ResultLine.Text = "检查引擎未就绪（等待运行期接线），不假数据。";
            return;
        }

        var (source, roots) = SourceProvider();
        _vm.Load(source, roots);
        Render();
        ResultLine.Text = $"检查完成：候选 {_vm.CleanupCandidates.Count} 项，待确认 {_vm.Pending.Count} 项。";
    }

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        if (CleanupExecutor is null || _vm.CleanupCandidates.Count == 0)
        {
            ResultLine.Text = _vm.CleanupCandidates.Count == 0 ? "没有可清理的候选项。" : "清理执行器未就绪。";
            return;
        }

        ScanRing.Visibility = Visibility.Visible;
        try
        {
            await CleanupExecutor([.. _vm.CleanupCandidates.Select(c => c.Path)]).ConfigureAwait(true);
            ResultLine.Text = "清理已执行（先进快照与回收站，30 天内可在时光回溯还原）。";
        }
        finally
        {
            ScanRing.Visibility = Visibility.Collapsed;
        }
    }

    private void Render()
    {
        CandidateList.Items.Clear();
        foreach (var row in _vm.CleanupCandidates)
        {
            CandidateList.Items.Add(new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Text = $"{row.KindLabel}｜{row.Path}",
            });
        }
        CandidateEmpty.Visibility = _vm.CleanupCandidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        PendingList.Items.Clear();
        foreach (var row in _vm.Pending)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Text = $"{row.KindLabel}｜{row.Path}｜修改于 {row.LastModifiedLocal ?? "未知"}",
            });
            var keep = new Button
            {
                Content = "保留",
                FontSize = 11,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 4, 12, 4),
            };
            var path = row.Path;
            keep.Click += (_, _) =>
            {
                _vm.Keep(path); // 保留项不再提示（R-P14-1 原型 flowPending 口径）
                Render();
            };
            panel.Children.Add(keep);
            PendingList.Items.Add(panel);
        }
        PendingEmpty.Visibility = _vm.Pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLine.Visibility = Visibility.Collapsed;
    }
}
