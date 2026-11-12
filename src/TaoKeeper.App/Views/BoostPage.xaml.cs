using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.App.Views;

/// <summary>
/// 性能加速页 code-behind：数据经 SpeedupPageViewModel 注入（Bind），
/// 未刷新/未接线时按未测态如实展示。建议只列不代做——
/// 勾选进确认集合，点执行才交 VM 确认闸门逐条处理（未勾绝不进执行清单）。
/// 进入页面时若适配器已接线但计划未刷新，自动跑一次真实计划（注册表/启动文件夹实况）；
/// 开机耗时为事件日志口径读数（近 3 次中位数，不可读=未测态）。
/// </summary>
public sealed partial class BoostPage : Page
{
    private SpeedupPageViewModel _vm = new();
    private readonly HashSet<string> _confirmedIds = new();

    /// <summary>当前内存快照提供者（宿主接线；加速计划的内存瘦身建议判定用）。</summary>
    public static Func<MemorySnapshot?>? CurrentMemoryProvider { get; set; }

    /// <summary>开机耗时读数（宿主接线；事件日志口径近 3 次毫秒值，新→旧；不可读为空清单）。</summary>
    public static Func<IReadOnlyList<long>>? BootTimesProvider { get; set; }

    public BoostPage()
    {
        InitializeComponent();
        Bind(new SpeedupPageViewModel());
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 主窗在导航参数里带 VM（运行期接线前为 null → 未测态兜底）
        if (e.Parameter is SpeedupPageViewModel vm)
        {
            Bind(vm);
            if (vm.Plan is null) _ = RefreshPlanIfNeededAsync(vm);
        }
        RefreshBootTimeLine();
    }

    /// <summary>计划未刷新时自动跑一次真实计划（只读：列建议不执行）；完成后回 UI 线程重绑。</summary>
    private async System.Threading.Tasks.Task RefreshPlanIfNeededAsync(SpeedupPageViewModel vm)
    {
        try
        {
            await vm.RefreshPlanAsync(CurrentMemoryProvider?.Invoke()).ConfigureAwait(true);
            // 完成后重绑：计划就绪即换出建议清单（仅当用户仍停留本页）
            if (ReferenceEquals(_vm, vm)) Bind(vm);
        }
        catch (Exception)
        {
            // 计划刷新失败保持未测态；用户可再进本页重试，不假数据
        }
    }

    /// <summary>P5-2：开机耗时读数（事件日志口径，3 次中位数；不可读=未测态如实说明）。</summary>
    private void RefreshBootTimeLine()
    {
        var times = BootTimesProvider?.Invoke() ?? Array.Empty<long>();
        BootTimeText.Text = times.Count == 0
            ? "开机耗时：未测（本机事件日志不可读，需要诊断性能日志权限；不做估算）"
            : $"开机耗时（事件日志口径，近 {times.Count} 次）："
              + string.Join(" / ", times) + " 毫秒，中位数 " + times.OrderBy(t => t).ElementAt(times.Count / 2) + " 毫秒（数字可复现）";
    }

    /// <summary>注入加速数据（未刷新态经 VM 层保证如实，不假建议数）。</summary>
    public void Bind(SpeedupPageViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        _confirmedIds.Clear();

        // 进程占用排行：直接消费 Core 既有 StartupProcessSnapshot.Processes 契约
        //（VM 未接线快照为空 → 如实空置，不假数据）；与体检计划刷新状态相互独立
        var ranking = vm.ProcessRanking;
        ProcessEmpty.Visibility = ranking.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProcessList.Items.Clear();
        foreach (var process in ranking)
            ProcessList.Items.Add(SpeedupPageViewModel.Describe(process));

        var plan = vm.Plan;
        if (plan is null)
        {
            SuggestHeader.Text = "启动项建议";
            SuggestEmpty.Visibility = Visibility.Visible;
            SuggestList.Items.Clear();
            ApplyButton.Visibility = Visibility.Collapsed;
            ReceiptText.Text = string.Empty;
            MemTrimLine.Text = "尚未体检加速，暂无建议（等待运行期接线；不假数据）。";
            return;
        }

        SuggestHeader.Text = $"启动项建议 · {plan.SummaryText}";
        SuggestEmpty.Visibility = Visibility.Collapsed;
        SuggestList.Items.Clear();

        foreach (var suggestion in vm.DisableSuggestions)
            SuggestList.Items.Add(BuildSuggestionRow(suggestion, "建议禁用"));
        foreach (var suggestion in vm.ReviewSuggestions)
            SuggestList.Items.Add(BuildSuggestionRow(suggestion, "自行判断"));

        ApplyButton.Visibility = SuggestList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReceiptText.Text = string.Empty;
        MemTrimLine.Text = plan.MemoryTrimSuggested
            ? $"建议释放内存：{plan.MemoryNote ?? SpeedupPolicy.MemoryTrimReason}"
            : "本轮无内存瘦身建议。";
    }

    private StackPanel BuildSuggestionRow(SpeedupSuggestion suggestion, string bandText)
    {
        var check = new CheckBox { IsChecked = false, MinWidth = 0 };
        check.Checked += (_, _) => _confirmedIds.Add(suggestion.Item.Id);
        check.Unchecked += (_, _) => _confirmedIds.Remove(suggestion.Item.Id);

        var meta = $"{bandText} · {suggestion.Reason}"
            + (suggestion.Item.OwningProductId is { } owner ? $" · 本产品组件（{owner}）" : string.Empty);

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                check,
                new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = suggestion.Item.Name,
                            FontSize = 13.5,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = ThemeBrush("TaoInkBrush"),
                        },
                        new TextBlock
                        {
                            Text = meta,
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = ThemeBrush("TaoInk2Brush"),
                        },
                    },
                },
            },
        };
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        // 确认闸门：空勾选直接返回；执行结果逐条转述，不做任何勾选外动作
        try
        {
            var results = await _vm.ApplyConfirmedAsync(_confirmedIds).ConfigureAwait(true);
            ReceiptText.Text = results.Count == 0
                ? "未勾选任何项，未执行任何动作。"
                : string.Join("；", results.Select(static r => $"{r.ItemId}:{r.DisplayText}"));
        }
        catch (Exception ex)
        {
            // async void 处理器不得让异常逃逸（逃逸即 WinUI stowed exception 闪退）；
            // 失败如实显示原文，不夸大也不静默。
            ReceiptText.Text = $"执行未完成：{ex.Message}";
        }
    }

    private void OnStartupListClick(object sender, RoutedEventArgs e)
    {
        // 启动项只读清单：不占独立路由，直达只读页；宿主接线了数据源则带上 VM，未接线为未测态
        Frame.Navigate(typeof(StartupPage), StartupPage.ViewModelProvider?.Invoke());
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
