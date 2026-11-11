using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using TaoKeeper.AppKernel.ViewModels;

namespace TaoKeeper.App.Views;

/// <summary>
/// 桌面挂件页 code-behind：Bind(vm) 注入挂件一帧数据。
/// 道系边界：未就绪/过期一律灰显如实（不假内存百分比、不假垃圾体积）；
/// 环图弧长=内存占用百分比（无数据不出弧）；「一键清理」只打开确认浮层并置待确认态，
/// 「清理（进回收站）」执行通道随运行期接线启用，未接前如实禁用，本页零写动作。
/// </summary>
public sealed partial class WidgetPage : Page
{
    private WidgetPageViewModel? _vm;

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

        // 明细如实：挂件帧只带概览不带逐项明细，扫描数据未就绪就明说，不编明细行
        if (_vm.LatestFrame is { } f)
        {
            DetailList.Text = f.JunkCandidateBytes is { } bytes && f.JunkCandidateCount is { } count
                ? $"垃圾候选约 {bytes / 1024.0 / 1024:F1} MB（{count} 项）——逐项明细以磁盘清理页扫描结果为准。"
                : "垃圾明细未就绪：先在磁盘清理页完成一次扫描。";
            OkClean.IsEnabled = false; // 执行通道随运行期接线启用，未接前如实禁用
        }
        else
        {
            DetailList.Text = "垃圾明细未就绪：先在磁盘清理页完成一次扫描。";
            OkClean.IsEnabled = false;
        }

        ConfirmLayer.Visibility = Visibility.Visible;
    }

    private void OnCancelClean(object sender, RoutedEventArgs e)
    {
        _vm?.CompleteCleanRequest(); // 确认浮层走完解除待确认态
        ConfirmLayer.Visibility = Visibility.Collapsed;
    }

    private void OnOkClean(object sender, RoutedEventArgs e)
    {
        // 执行通道随运行期接线启用（CleanupCoordinator）；未接前按钮已禁用，此处理论不可达。
        // 若可达说明接线层已启用本页执行入口，此处仍不落任何写动作，转由接线层编排。
        _vm?.CompleteCleanRequest();
        ConfirmLayer.Visibility = Visibility.Collapsed;
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
