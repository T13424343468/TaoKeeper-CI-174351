using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.App.Views;

/// <summary>
/// 桌面挂件页 code-behind：Bind(vm) 注入挂件一帧数据（宿主接线真实内存快照/垃圾概览/进程概览）。
/// 道系边界：未就绪/过期一律灰显如实（不假内存百分比、不假垃圾体积）；
/// 环图弧长=内存占用百分比（无数据不出弧）；「一键清理」先过明细确认浮层，
/// 确认后走与主程序同一 CleanupExecutor（回收站语义，P11-3），完成后按真实回执报数。
/// </summary>
public sealed partial class WidgetPage : Page
{
    private WidgetPageViewModel? _vm;

    /// <summary>垃圾候选提供者（宿主接线；来自最近一次真实垃圾扫描，未扫描为空清单）。</summary>
    public static Func<System.Collections.Generic.IReadOnlyList<CleanupActionItem>>? JunkCandidatesProvider { get; set; }

    /// <summary>清理执行（宿主接线；与主程序同一 CleanupCoordinator，回收站语义）。</summary>
    public static Func<System.Collections.Generic.IReadOnlyList<CleanupActionItem>,
        System.Threading.Tasks.Task<CleanupBatchResult>>? CleanupExecutorProvider { get; set; }

    public WidgetPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is WidgetPageViewModel vm) Bind(vm);
    }

    /// <summary>注入挂件数据（未注入时保持未就绪灰显态，不假数据）。</summary>
    public void Bind(WidgetPageViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        Refresh();
    }

    private void Refresh()
    {
        if (_vm is null) return;

        var hasFreshData = _vm.LatestFrame is { } && !_vm.DataStale;

        // 灰显口径：无数据或过期整帧压暗（原型灰显语义，如实不展示旧数）
        WidgetMock.Opacity = hasFreshData ? 1.0 : 0.45;

        // 环图中心：内存占用（数据驱动；未知即如实写「内存占用未知」）
        RingPct.Text = string.IsNullOrEmpty(_vm.MemoryText) ? "内存占用未知" : _vm.MemoryText;
        RingSub.Text = string.IsNullOrEmpty(_vm.BackgroundText) ? "内存明细随运行期接线" : _vm.BackgroundText;

        // 垃圾概览：VM 口径如实（未就绪即「垃圾概览未就绪」）
        JunkLine.Text = _vm.JunkText;

        // 环图弧长 = 内存占用百分比（有数据且新鲜才画）
        var pct = hasFreshData ? _vm.LatestFrame!.MemoryUsedPercent : 0;
        DrawRing(Math.Clamp(pct, 0, 100) / 100.0);
    }

    private void OnCleanEntryClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        // 预检：永远要求确认（VM 置待确认态并返回确认提示）
        var hint = _vm.PrepareCleanRequest();
        DetailNote.Text = hint;

        // 明细如实：候选来自最近一次真实垃圾扫描；未扫描就明说，不编明细行
        var candidates = JunkCandidatesProvider?.Invoke() ?? Array.Empty<CleanupActionItem>();
        if (candidates.Count > 0)
        {
            DetailList.Text = $"垃圾候选 {candidates.Count} 项（约 {candidates.Sum(c => c.SizeBytes) / 1024.0 / 1024:F1} MB）——"
                + "全部进回收站，可还原；受保护目录会被闸门拒收。";
            OkClean.IsEnabled = CleanupExecutorProvider is not null;
        }
        else
        {
            DetailList.Text = CleanupExecutorProvider is null
                ? "清理执行通道未接线；未提供任何候选。"
                : "垃圾明细未就绪：先在磁盘清理页完成一次扫描（或稍后再试）。";
            OkClean.IsEnabled = false;
        }

        ConfirmLayer.Visibility = Visibility.Visible;
    }

    private void OnCancelClean(object sender, RoutedEventArgs e)
    {
        _vm?.CompleteCleanRequest(); // 确认浮层走完解除待确认态
        ConfirmLayer.Visibility = Visibility.Collapsed;
    }

    private async void OnOkClean(object sender, RoutedEventArgs e)
    {
        // P11-3：一键清理真实可执行——同一执行链、同回收站语义、同清理账（与主程序零语义差）
        if (OkClean is null) return;
        OkClean.IsEnabled = false;
        try
        {
            var items = (JunkCandidatesProvider?.Invoke() ?? Array.Empty<CleanupActionItem>())
                .Select(i => i with { Confirmed = true }) // 确认浮层即确认：候选逐条进确认态，闸门仍会拒受保护路径
                .ToList();
            if (items.Count == 0 || CleanupExecutorProvider is null)
            {
                DetailNote.Text = "没有可执行项或执行通道未接线；未动任何文件。";
                return;
            }
            var batch = await CleanupExecutorProvider(items);
            DetailNote.Text = $"清理完成：{batch.SucceededCount} 项（约 {batch.SucceededBytes / 1024.0 / 1024:F1} MB）已进回收站可还原；"
                + $"未成 {batch.FailedCount} 项、拒绝 {batch.RefusedCount} 项（受保护目录不碰）。";
        }
        catch (Exception ex)
        {
            DetailNote.Text = $"清理未完成：{ex.Message}（目标文件未动，可重试）";
        }
        finally
        {
            _vm?.CompleteCleanRequest();
            OkClean.IsEnabled = true;
            ConfirmLayer.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>画环图弧线：frac=内存占用比例（0 或未知时不画）。</summary>
    private void DrawRing(double frac)
    {
        if (frac <= 0)
        {
            RingArc.Data = null;
            return;
        }

        const double r = 48, cx = 60, cy = 60; // 对应原型 120×120 环
        var angle = Math.Clamp(frac, 0, 1) * 2 * Math.PI;
        var end = new Point(cx + r * Math.Sin(angle), cy - r * Math.Cos(angle));

        var figure = new PathFigure { StartPoint = new Point(cx, cy - r) };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(r, r),
            IsLargeArc = frac > 0.5,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        RingArc.Data = geo;
    }
}
