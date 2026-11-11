using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.App.Views;

/// <summary>
/// 内存管理页 code-behind：仪表条/趋势双折线/占用排行经 MemPageViewModel 绑定；
/// 页面文件方案按钮只做展示与说明（零写动作——应用设置属用户确认后的适配层动作，
/// 执行器经 PageFileApplyExecutor 接线，未接线时如实提示）。
/// </summary>
public sealed partial class MemPage : Page
{
    private MemPageViewModel? _vm;

    /// <summary>页面文件应用执行器（宿主接线；参数=方案键，返回给用户看的结果文案）。</summary>
    public static Func<string, string>? PageFileApplyExecutor { get; set; }

    public MemPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MemPageViewModel vm)
        {
            _vm = vm;
            Bind(vm);
        }
    }

    /// <summary>注入内存数据（未传时全空如实展示，不假占用率）。</summary>
    public void Bind(MemPageViewModel vm)
    {
        _vm = vm;
        Refresh();
    }

    /// <summary>合入一份新快照（监控循环刷新调用）。</summary>
    public void ApplySnapshot(MemorySnapshot snapshot)
    {
        if (_vm is null) _vm = new MemPageViewModel();
        _vm.ApplySnapshot(snapshot);
        Refresh();
    }

    private void Refresh()
    {
        if (_vm is null) return;

        // 物理内存仪表：UsageText=「62%」，DetailText=「物理内存 X / Y，页面文件 Z%」
        var percent = ParsePercent(_vm.UsageText);
        PhysBar.Value = percent ?? 0;
        PhysBar.IsIndeterminate = _vm.UsageText.Length == 0; // 未测态：如实不定态而非假 0
        PhysTitle.Text = $"物理内存 · 告警线 {_vm.AlertPercent}%";
        PhysDetail.Text = _vm.UsageText.Length == 0
            ? "尚未采集内存读数"
            : $"{_vm.UsageText} 已用 · {_vm.DetailText}";

        // 页面文件仪表：从快照趋势里取最新一个非空页面文件占用率（VM 趋势保底）
        var pf = _vm.Trend.Count > 0 ? _vm.Trend[^1].PageFileUsedPercent : null;
        PageFileBar.Value = pf ?? 0;
        PageFileBar.IsIndeterminate = pf is null;
        PageFileDetail.Text = pf is { } v ? $"已用 {v:F0}%" : "系统托管或未配置（占用未知）";

        DrawTrend();

        // 占用排行
        var top = _vm.TopProcesses;
        TopEmpty.Visibility = top.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TopList.ItemsSource = top.Select(t =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = t.ProcessName,
                FontSize = 13,
                MinWidth = 180,
                Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
            });
            row.Children.Add(new TextBlock
            {
                Text = FormatBytes(t.WorkingSetBytes),
                FontSize = 12.5,
                Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
            });
            return row;
        }).ToList();

        SelectPlan("smart"); // 默认智能推荐高亮（原型 .plan on）
    }

    private static double? ParsePercent(string text) =>
        text.EndsWith("%") && double.TryParse(text.AsSpan(0, text.Length - 1), out var v) ? v : null;

    private void DrawTrend()
    {
        TrendCanvas.Children.Clear();
        var trend = _vm?.Trend ?? Array.Empty<MemorySnapshot>();
        if (trend.Count < 2) return; // 数据不足如实留白（原型两折线，此处数据不足不假画）

        const double width = 600, height = 100;
        double Step(int i) => trend.Count == 1 ? 0 : width * i / (trend.Count - 1);
        double Y(double p) => height - height * p / 100.0;

        var phys = new Polyline
        {
            Stroke = (Brush)Application.Current.Resources["TaoBambooBrush"],
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        for (var i = 0; i < trend.Count; i++)
            phys.Points.Add(new Windows.Foundation.Point(Step(i), Y(trend[i].PhysicalUsedPercent)));
        TrendCanvas.Children.Add(phys);

        var withPf = trend.Where(t => t.PageFileUsedPercent is { }).ToList();
        if (withPf.Count >= 2)
        {
            var pfLine = new Polyline
            {
                Stroke = (Brush)Application.Current.Resources["TaoGoldBrush"],
                StrokeThickness = 1.8,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                Opacity = 0.7,
            };
            for (var i = 0; i < withPf.Count; i++)
                pfLine.Points.Add(new Windows.Foundation.Point(
                    width * i / (withPf.Count - 1),
                    Y(withPf[i].PageFileUsedPercent!.Value)));
            TrendCanvas.Children.Add(pfLine);
        }
    }

    private void OnPlanClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        SelectPlan(key);

        var note = key switch
        {
            "perf" => "性能优先：初始与上限都放大，减少动态扩展；改动需三步确认，改前自动备份原设置。",
            "space" => "空间优先：初始压小，给系统盘省空间；改动需三步确认，改前自动备份原设置。",
            "system" => "系统托管：交给 Windows 自动管理；应用需三步确认。",
            "custom" => "自定义：填初始与最大值（MB），校验通过才能继续；错误阻止应用、警告提示可继续。",
            _ => "智能推荐：按物理内存给初始与最大建议值；改动需三步确认，改前自动备份原设置，随时一键回滚；完全关闭页面文件需二次确认并明确警告。",
        };
        PlanNote.Text = note;

        // 应用方案=写系统设置：必须经用户确认后的执行器（未接线如实提示，绝不静默改系统）
        var executor = PageFileApplyExecutor;
        if (executor is null) return; // 未接线：只展示说明（不动任何系统设置）
        _ = ConfirmAndApplyAsync(key, executor);
    }

    private void SelectPlan(string key)
    {
        foreach (var (btn, k) in new[] { (PlanSmart, "smart"), (PlanPerf, "perf"), (PlanSpace, "space"), (PlanSystem, "system"), (PlanCustom, "custom") })
            btn.Background = k == key
                ? (Brush)Application.Current.Resources["TaoAzureWashBrush"]
                : null;
    }

    private async System.Threading.Tasks.Task ConfirmAndApplyAsync(string key, Func<string, string> executor)
    {
        var dialog = new ContentDialog
        {
            Title = "应用页面文件方案",
            Content = "改动需三步确认，改前自动备份原设置，随时一键回滚。现在进行第一步确认？",
            PrimaryButtonText = "继续",
            CloseButtonText = "先不",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await new ContentDialog
        {
            Title = "页面文件方案",
            Content = executor(key),
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
        return bytes >= GB ? $"{(double)bytes / GB:0.#} GB"
            : bytes >= MB ? $"{(double)bytes / MB:0.#} MB"
            : bytes >= KB ? $"{(double)bytes / KB:0.#} KB"
            : $"{bytes} B";
    }
}
