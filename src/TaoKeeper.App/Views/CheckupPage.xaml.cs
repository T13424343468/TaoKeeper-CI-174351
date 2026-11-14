using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.AppKernel.Navigation;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.App.Views;

/// <summary>
/// 体检中心页 code-behind：评分与发现清单经 CheckupPageViewModel 绑定（未测态如实「尚未体检」，不假 100 分）；
/// 环图弧长=得分/100（未测不出弧）；「去处理」按发现的 JumpRoute 路由跳转（无对应页面时如实提示不跳）。
/// R-P4-1 开始体检全链：「开始体检」→ Running 进度态（可取消）→ Completed 结果态 / Failed 原因态，
/// 状态流转经 VM.RunAsync 状态机（本页只呈现，不另起状态）。体检本身零写动作：只读评估。
/// </summary>
public sealed partial class CheckupPage : Page
{
    private CheckupPageViewModel? _vm;
    private CancellationTokenSource? _runCts;

    /// <summary>体检执行器（组合根接线：真实各维只读扫描→汇上下文；未接线=null → VM 如实 Failed）。</summary>
    public static Func<CancellationToken, Task<HealthContext>>? CheckupExecutor { get; set; }

    public CheckupPage()
    {
        InitializeComponent();
        Bind(new CheckupPageViewModel().RunCheckup(HealthContext.Empty)); // 未测兜底：不假分不假发现
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is CheckupPageViewModel vm)
        {
            vm.PhaseChanged = _ => RefreshRunState();
            Bind(vm);
            RefreshRunState();
        }
    }

    /// <summary>注入体检展示态（未传时按未测态如实展示）。</summary>
    public void Bind(CheckupPageViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        vm.PhaseChanged = _ => RefreshRunState();
        Refresh();
        RefreshRunState();
    }

    /// <summary>R-P4-1：开始体检（Running 相位由 VM 状态机拒绝重入，本页只呈现相位）。</summary>
    private async void OnStartCheckup(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.RunPhase == CheckupRunPhase.Running) return;
        _runCts = new CancellationTokenSource();
        await _vm.RunAsync(CheckupExecutor, _runCts.Token);
        Refresh(); // 结果/失败态回到发现清单（VM.CompleteRun/FailRun 后相位回调已触发进度条收起）
    }

    /// <summary>取消体检：令牌入执行器，未取到的维度如实缺席（零写动作，无半截改动）。</summary>
    private void OnCancelRun(object sender, RoutedEventArgs e) => _runCts?.Cancel();

    /// <summary>按 VM 相位呈现进度态（Running=环动+取消可见；Failed=原因如实；其余收起）。</summary>
    private void RefreshRunState()
    {
        if (_vm is null) return;
        var running = _vm.RunPhase == CheckupRunPhase.Running;
        StartCheckupButton.IsEnabled = !running;
        RunRing.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelRunButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunStatus.Visibility = _vm.RunPhase == CheckupRunPhase.Failed ? Visibility.Visible : Visibility.Collapsed;
        RunStatus.Text = _vm.FailureMessage ?? string.Empty;
    }

    private void Refresh()
    {
        if (_vm is null) return;

        // 环图：未测不出弧不假分；已测弧长=得分/100（竹青随主题）
        if (_vm.IsUntested)
        {
            ScoreText.Text = "未测";
            ScoreBand.Text = "尚未体检";
            RingNote.Text = "各模块接入后自动出分（未测维度不扣分）。";
            ScoreArc.Visibility = Visibility.Collapsed;
        }
        else
        {
            ScoreText.Text = _vm.Score.ToString();
            ScoreBand.Text = _vm.ScoreBandText;
            RingNote.Text = $"体检得分 · {_vm.ScoreBandText}";
            var stroke = 14;
            const double diameter = 174;
            var circumference = 2 * Math.PI * (diameter - stroke) / 2;
            var fraction = Math.Clamp(_vm.Score, 0, 100) / 100.0;
            ScoreArc.Visibility = Visibility.Visible;
            ScoreArc.StrokeDashArray = new DoubleCollection
            {
                fraction * circumference / stroke, // XAML 虚线单位=描边厚度倍数
                circumference / stroke + 1,
            };
            ScoreArc.RenderTransform = new RotateTransform { Angle = -90, CenterX = diameter / 2, CenterY = diameter / 2 };
        }

        // 发现清单（按严重度降序；严重度配色与 VM.AccentColorOf 同口径）
        var findings = _vm.Findings;
        FindingHeader.Text = _vm.IsUntested ? "发现 · 未测" : $"发现 {findings.Count} 项";
        FindingEmpty.Visibility = findings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FindingEmpty.Text = _vm.IsUntested
            ? "尚未体检。各模块扫描接线后，这里给出六维发现清单（按严重度降序，未测维度不误扣分）。"
            : "本轮没有发现项。";
        FindingRows.ItemsSource = findings.Select(MakeRow).ToList();

        // G8/R-P4-9 老机器建议卡（只建议不静默改；实测依据同屏，未探测为空清单）
        var advice = _vm.AdviceCards;
        AdviceHeader.Visibility = advice.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AdviceRows.ItemsSource = advice.Select(static card => new StackPanel
        {
            Spacing = 2,
            Padding = new Thickness(0, 7, 0, 7),
            Children =
            {
                new TextBlock
                {
                    Text = "建议 · " + card.Title,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
                },
                new TextBlock
                {
                    Text = card.Detail,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
                },
                new TextBlock
                {
                    Text = card.Evidence,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TaoInk3Brush"],
                },
            },
        }).ToList();
    }

    private UIElement MakeRow(CheckupFindingItem finding)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            Background = SeverityBrush(finding.Severity),
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = finding.Title,
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
        });
        info.Children.Add(new TextBlock
        {
            Text = finding.Detail,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        // 有对应页面才出「去处理」；无路由的发现只展示（如实，不假跳转）
        if (finding.JumpRoute is { } route)
        {
            var button = new Button
            {
                Content = "去处理",
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 5, 12, 5),
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Click += (_, _) => OnFindingJump(route);
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }

        return grid;
    }

    private void OnFindingJump(AppRoute route)
    {
        var shell = (App.Current as App)?.Shell;
        if (shell is null) return;
        shell.SelectRoute(route); // 目标页未注册时主窗显示如实占位（P2 注册后直达）
    }

    // 与 VM.AccentColorOf 同口径：问题=朱砂、建议=金、提示=墨灰（画刷随主题即时取值）
    private Brush SeverityBrush(HealthSeverity severity) => severity switch
    {
        HealthSeverity.Critical => (Brush)Application.Current.Resources["TaoCinnabarBrush"],
        HealthSeverity.Warning => (Brush)Application.Current.Resources["TaoCinnabarBrush"],
        HealthSeverity.Advice => (Brush)Application.Current.Resources["TaoGoldBrush"],
        _ => (Brush)Application.Current.Resources["TaoInk3Brush"],
    };
}
