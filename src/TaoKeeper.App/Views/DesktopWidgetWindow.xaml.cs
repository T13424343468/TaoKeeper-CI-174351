using System;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using TaoKeeper.AppKernel.Settings;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Models;

namespace TaoKeeper.App.Views;

/// <summary>
/// 桌面挂件独立悬浮窗（派活口径落地，非页内示意卡）：
/// 无边框无标题栏置顶小窗（摘圈口径：悬浮无外圈框线），常驻桌面一角不进任务栏；
/// 数据帧经 <see cref="FrameProvider"/>（CompositionRoot 接挂件页同一 ViewModel，未就绪灰显不编数）；
/// 一键清理复用 WidgetPage 同一执行链（回收站语义，先过窗内明细确认浮层）；
/// 关闭/收起即彻底退出：停读帧定时器、清静态引用，零后台残留。
/// </summary>
public sealed partial class DesktopWidgetWindow : Window
{
    /// <summary>数据帧提供者（CompositionRoot 接线：挂件页同一帧缓存；未接线=null → 挂件如实显示未就绪）。</summary>
    public static Func<(WidgetFrame? Frame, bool DataStale)?>? FrameProvider { get; set; }

    /// <summary>打开主窗处理（App 接线；右键菜单「打开道管家」）。</summary>
    public static Action? OpenShellHandler { get; set; }

    private const int StandardWidth = 260, StandardHeight = 372;
    private const int CompactWidth = 220, CompactHeight = 300;
    private const int ScreenMargin = 16;

    private DispatcherTimer? _frameTimer;
    private AppSettings _config = new();

    private DesktopWidgetWindow()
    {
        InitializeComponent();
        ConfigureFramelessAlwaysOnTop();
        this.Closed += (_, _) =>
        {
            // 退出零残留：停读帧定时器、清静态引用（窗口销毁后定时器不再跑）
            if (_frameTimer is { } t) { t.Stop(); _frameTimer = null; }
            if (ReferenceEquals(_current, this)) _current = null;
        };
    }

    private static DesktopWidgetWindow? _current;

    /// <summary>按当前设置显隐挂件窗（存在则更新位置与配置；收起=Close 零残留，重开即新建）。</summary>
    public static void ApplySettings(AppSettings config)
    {
        if (config is null) return;
        if (!config.ShowDesktopWidget)
        {
            _current?.Close(); // 收起即彻底退出：停定时器、清引用
            _current = null;
            return;
        }
        if (_current is { } w) { w._config = config; w.ApplySize(config); return; }
        _current = new DesktopWidgetWindow { _config = config };
        _current.Activate();
        _current.StartFrameLoop();
    }

    /// <summary>无边框置顶小窗（摘圈口径）：去标题栏边框、置顶、不可拖缩放、不进任务栏与 Alt+Tab。</summary>
    private void ConfigureFramelessAlwaysOnTop()
    {
        if (AppWindow is not { } appWindow) return;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false); // 无边框无标题栏
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        appWindow.IsShownInSwitchers = false; // 不进任务栏与 Alt+Tab（常驻桌面一角，不抢窗口位）
        appWindow.Title = "道管家 · 桌面挂件";
    }

    /// <summary>按设置落位：标准/紧凑尺寸，常驻主屏工作区右上角。</summary>
    private void ApplySize(AppSettings config)
    {
        if (AppWindow is not { } appWindow) return;
        var (w, h) = config.WidgetSize == WidgetSize.Compact
            ? (CompactWidth, CompactHeight)
            : (StandardWidth, StandardHeight);
        appWindow.Resize(new SizeInt32(w, h));

        var area = DisplayArea.Primary.WorkArea;
        var x = Math.Max(0, area.Width - w - ScreenMargin);
        appWindow.Move(new PointInt32(x, ScreenMargin));
    }

    /// <summary>读帧循环：2s 一帧（与挂件页同一帧缓存，未就绪/过期灰显）。</summary>
    private void StartFrameLoop()
    {
        ApplySize(_config);
        PlayPopInAnimation(); // 摘圈弹出动画：环与卡片从轻缩放+透明浮出
        _frameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _frameTimer.Tick += (_, _) => Refresh();
        _frameTimer.Start();
        Refresh();
    }

    /// <summary>摘圈弹出动画：250ms 缓动从 0.6 缩放+透明浮出（无外圈框线的悬浮观感）。</summary>
    private void PlayPopInAnimation()
    {
        PopScale.ScaleX = PopScale.ScaleY = 0.6;
        WidgetCard.Opacity = 0;
        var storyboard = new Storyboard();
        var scale = new DoubleAnimation { From = 0.6, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(250)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation { From = 0.0, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(250)) };
        Storyboard.SetTarget(scale, PopScale);
        Storyboard.SetTargetProperty(scale, "ScaleX");
        Storyboard.SetTarget(fade, WidgetCard);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(scale);
        storyboard.Children.Add(fade);

        // ScaleX/ScaleY 同步：X 动画已加，Y 复用同一 storyboard 的第二段
        var scaleY = new DoubleAnimation { From = 0.6, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(250)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(scaleY, PopScale);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        storyboard.Children.Add(scaleY);
        storyboard.Begin();
    }

    /// <summary>按当前帧刷新读数（未就绪/过期一律灰显如实，不编数）。</summary>
    private void Refresh()
    {
        var provided = FrameProvider?.Invoke();
        var frame = provided?.Frame;
        var stale = provided?.DataStale != false;
        var hasFresh = frame is { } && !stale;

        WidgetCard.Opacity = hasFresh ? 1.0 : 0.55;

        var pct = hasFresh ? Math.Clamp(frame!.MemoryUsedPercent, 0, 100) : 0;
        RingPct.Text = hasFresh ? $"{pct:F0}%" : "内存占用未知";
        RingSub.Text = frame is { BackgroundProcessCount: { } n } && hasFresh
            ? $"后台进程 {n} 个"
            : "内存明细待首次读数";

        // 垃圾待清行（配置勾选才显示；未就绪如实说明）
        if (_config.WidgetContents.Contains(WidgetContent.JunkPending))
        {
            JunkLine.Text = hasFresh && frame is { JunkCandidateBytes: { } bytes, JunkCandidateCount: { } count }
                ? $"垃圾候选约 {Fmt(bytes)}（{count} 项）"
                : "垃圾概览未就绪";
            JunkLine.Visibility = Visibility.Visible;
        }
        else JunkLine.Visibility = Visibility.Collapsed;

        // 磁盘余量行（配置勾选才显示；真实读数经 DriveInfo，读不到如实说明）
        if (_config.WidgetContents.Contains(WidgetContent.DiskFree))
        {
            DiskLine.Text = CDriveFreeLine();
            DiskLine.Visibility = Visibility.Visible;
        }
        else DiskLine.Visibility = Visibility.Collapsed;

        DrawRing(hasFresh ? pct / 100.0 : 0);
    }

    private static string CDriveFreeLine()
    {
        try
        {
            var drive = new DriveInfo("C");
            if (!drive.IsReady || drive.AvailableFreeSpace <= 0) return "磁盘余量未就绪";
            return $"C 盘剩余 {drive.AvailableFreeSpace / 1024.0 / 1024 / 1024:F1} GB";
        }
        catch (Exception)
        {
            return "磁盘余量未就绪"; // 读不到如实说明，不编数
        }
    }

    private static string Fmt(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F1} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F0} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>画环图弧线：frac=内存占用比例（0 或未知时不画）；摘圈口径：无外圈框线。</summary>
    private void DrawRing(double frac)
    {
        if (frac <= 0)
        {
            RingArc.Data = null;
            return;
        }

        const double r = 48, cx = 60, cy = 60; // 对应挂件页 120×120 环
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

    // ── 右键菜单 ──

    private void OnOpenMainClick(object sender, RoutedEventArgs e) => OpenShellHandler?.Invoke();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    private void OnHideClick(object sender, RoutedEventArgs e) => Close(); // 收起即彻底退出：零定时器、零引用残留

    // ── 一键清理（与挂件页/主程序同一执行链：先过明细确认浮层，回收站语义） ──

    private void OnCleanEntryClick(object sender, RoutedEventArgs e)
    {
        // 候选来自最近一次真实垃圾扫描（同一提供者）；未扫描就明说，不编明细行
        var candidates = WidgetPage.JunkCandidatesProvider?.Invoke() ?? Array.Empty<CleanupActionItem>();
        if (candidates.Count > 0)
        {
            DetailList.Text = $"垃圾候选 {candidates.Count} 项（约 {candidates.Sum(c => c.SizeBytes) / 1024.0 / 1024:F1} MB）——"
                + "全部进回收站，可还原；受保护目录会被闸门拒收。";
            OkClean.IsEnabled = WidgetPage.CleanupExecutorProvider is not null;
        }
        else
        {
            DetailList.Text = WidgetPage.CleanupExecutorProvider is null
                ? "清理执行通道未接线；未提供任何候选。"
                : "垃圾明细未就绪：先在磁盘清理页完成一次扫描（或稍后再试）。";
            OkClean.IsEnabled = false;
        }
        ConfirmLayer.Visibility = Visibility.Visible;
    }

    private void OnCancelClean(object sender, RoutedEventArgs e) => ConfirmLayer.Visibility = Visibility.Collapsed;

    private async System.Threading.Tasks.Task RunCleanAsync()
    {
        OkClean.IsEnabled = false;
        try
        {
            var items = (WidgetPage.JunkCandidatesProvider?.Invoke() ?? Array.Empty<CleanupActionItem>())
                .Select(i => i with { Confirmed = true }) // 窗内确认即确认：闸门仍会拒受保护路径
                .ToList();
            if (items.Count == 0 || WidgetPage.CleanupExecutorProvider is null)
            {
                DetailNote.Text = "没有可执行项或执行通道未接线；未动任何文件。";
                return;
            }
            var batch = await WidgetPage.CleanupExecutorProvider(items);
            DetailNote.Text = $"清理完成：{batch.SucceededCount} 项（约 {batch.SucceededBytes / 1024.0 / 1024:F1} MB）已进回收站可还原；"
                + $"未成 {batch.FailedCount} 项、拒绝 {batch.RefusedCount} 项（受保护目录不碰）。";
            Refresh(); // 清理后读数按实际刷新
        }
        catch (Exception ex)
        {
            DetailNote.Text = $"清理未完成：{ex.Message}（目标文件未动，可重试）";
        }
        finally
        {
            OkClean.IsEnabled = true;
            ConfirmLayer.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOkClean(object sender, RoutedEventArgs e) => _ = RunCleanAsync();
}
