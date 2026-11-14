using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules.IncidentReplay;

namespace TaoKeeper.App.Views;

/// <summary>
/// P15 事发回放 code-behind：事件流由组合根接线（本机快照链事务 + Windows 事件日志只读适配器）。
/// 零外发：本页零网络引用（A15-3），时间窗 72h 默认可切（R-P15-2）。
/// </summary>
public sealed partial class IncidentReplayPage : Page
{
    private readonly IncidentReplayPageViewModel _vm = new();
    private DateTimeOffset _symptomTimeUtc = DateTimeOffset.UtcNow;

    /// <summary>事件流提供者（组合根接线；未接线时如实提示不可用，不假数据）。</summary>
    public static Func<IEnumerable<IncidentEvent>>? EventsProvider { get; set; }

    public IncidentReplayPage() => InitializeComponent();

    private void OnLoad(object sender, RoutedEventArgs e) => Render();

    private void OnWindow24(object sender, RoutedEventArgs e) => SwitchWindow(24);
    private void OnWindow72(object sender, RoutedEventArgs e) => SwitchWindow(72);
    private void OnWindow168(object sender, RoutedEventArgs e) => SwitchWindow(168);

    private void OnGranDay(object sender, RoutedEventArgs e) => SwitchGranularity("day");
    private void OnGranWeek(object sender, RoutedEventArgs e) => SwitchGranularity("week");

    private void SwitchGranularity(string granularity)
    {
        GranDayButton.Style = granularity == "day" ? (Style)Application.Current.Resources["TaoButtonPrimary"] : (Style)Application.Current.Resources["TaoButtonGhost"];
        GranWeekButton.Style = granularity == "week" ? (Style)Application.Current.Resources["TaoButtonPrimary"] : (Style)Application.Current.Resources["TaoButtonGhost"];
        if (EventsProvider is null) return;
        if (_vm.Timeline.Count == 0 && !_vm.IsEmptyWindow) return; // 尚未加载：只记档位不预填（V-A10）
        _vm.SetGranularity(LoadEvents(), _symptomTimeUtc, granularity);
        Render();
    }

    private void SwitchWindow(int hours)
    {
        W24Button.Style = hours == 24 ? (Style)Application.Current.Resources["TaoButtonPrimary"] : (Style)Application.Current.Resources["TaoButtonGhost"];
        W72Button.Style = hours == 72 ? (Style)Application.Current.Resources["TaoButtonPrimary"] : (Style)Application.Current.Resources["TaoButtonGhost"];
        W168Button.Style = hours == 168 ? (Style)Application.Current.Resources["TaoButtonPrimary"] : (Style)Application.Current.Resources["TaoButtonGhost"];
        _vm.SetWindow(LoadEvents(), _symptomTimeUtc, hours);
        Render();
    }

    private IEnumerable<IncidentEvent> LoadEvents() =>
        EventsProvider?.Invoke() ?? Enumerable.Empty<IncidentEvent>();

    private void Render()
    {
        if (EventsProvider is null)
        {
            ResultUnavailable();
            return;
        }

        var events = LoadEvents();
        if (!_vm.HasAttribution && _vm.Timeline.Count == 0 && !_vm.IsEmptyWindow)
        {
            _vm.Load(events, _symptomTimeUtc); // 首次加载：默认 72h
        }
        else if (_vm.Timeline.Count == 0 && !_vm.IsEmptyWindow)
        {
            _vm.Load(events, _symptomTimeUtc, _vm.WindowHours);
        }

        if (_vm.IsEmptyWindow)
        {
            EmptyWindowText.Visibility = Visibility.Visible;
            EmptyWindowText.Text = _vm.EmptyWindowText; // 空窗显式提示，不出空白列表（A15-2）
        }
        else
        {
            EmptyWindowText.Visibility = Visibility.Collapsed;
        }

        TimelineList.Items.Clear();
        if (_vm.Granularity == "week" && _vm.TimelineGroups.Count > 0)
        {
            // 按周分组渲染（R-P15-4）：组头=周起日期，组内按时间倒序
            foreach (var group in _vm.TimelineGroups)
            {
                TimelineList.Items.Add(new TextBlock
                {
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Text = group.Label,
                });
                foreach (var row in group.Rows)
                {
                    TimelineList.Items.Add(new TextBlock
                    {
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Text = $"    {row.TimeLocal}｜{row.SourceLabel}｜{row.Summary}",
                    });
                }
            }
        }
        else
        {
            foreach (var row in _vm.Timeline)
            {
                TimelineList.Items.Add(new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"{row.TimeLocal}｜{row.SourceLabel}｜{row.Summary}",
                });
            }
        }

        CauseLine.Text = _vm.MostLikelyCause ?? string.Empty;
        StepList.Items.Clear();
        foreach (var step in _vm.VerificationSteps)
        {
            StepList.Items.Add(new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Text = $"· {step}",
            });
        }
        ConfidenceLine.Text = _vm.HasAttribution ? $"置信度：{_vm.Confidence:P0}" : string.Empty;
        EmptyLine.Visibility = _vm.Timeline.Count > 0 || _vm.IsEmptyWindow ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResultUnavailable()
    {
        EmptyLine.Text = "事件流未就绪（等待运行期接线），不假数据。";
        EmptyLine.Visibility = Visibility.Visible;
    }
}
