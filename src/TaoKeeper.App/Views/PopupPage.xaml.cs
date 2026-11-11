using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.App.Views;

/// <summary>
/// 弹窗管理页 code-behind：规则逐条开关（ToggleSwitch → VM.SetRuleEnabled）、
/// 白名单移出（VM.RemoveWhitelist）、拦截记录回溯（VM.Describe）。
/// 合规形态：本页只维护规则与展示记录，不做任何窗口劫持动作。
/// </summary>
public sealed partial class PopupPage : Page
{
    private PopupPageViewModel? _vm;

    public PopupPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is PopupPageViewModel vm)
        {
            _vm = vm;
            Bind(vm);
        }
    }

    /// <summary>注入弹窗规则数据（未传时全空如实展示）。</summary>
    public void Bind(PopupPageViewModel vm)
    {
        _vm = vm;
        Refresh();
    }

    private void Refresh()
    {
        if (_vm is null) return;
        SummaryText.Text = _vm.SummaryText;

        // 规则清单：逐条开关（合规形态，每条独立可控）
        RuleList.ItemsSource = _vm.Rules.Select(rule =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = rule.Title,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
            });
            info.Children.Add(new TextBlock
            {
                Text = $"匹配 {rule.Pattern}",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
            });
            var sw = new ToggleSwitch
            {
                IsOn = rule.Enabled,
                OnContent = "开",
                OffContent = "关",
                Margin = new Thickness(12, 0, 0, 0),
            };
            sw.Toggled += (_, _) =>
            {
                _vm.SetRuleEnabled(rule.Id, sw.IsOn);
                Refresh();
            };
            row.Children.Add(info);
            row.Children.Add(sw);
            return row;
        }).ToList();

        // 白名单
        var wl = _vm.Whitelist;
        WhitelistEmpty.Visibility = wl.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WhitelistList.ItemsSource = wl.Select(entry =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = entry.Note ?? entry.Pattern,
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
            });
            info.Children.Add(new TextBlock
            {
                Text = $"匹配 {entry.Pattern}",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
            });
            var btn = new Button
            {
                Content = "移出",
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 4, 12, 4),
            };
            btn.Click += (_, _) =>
            {
                _vm.RemoveWhitelist(entry.Id);
                Refresh();
            };
            row.Children.Add(info);
            row.Children.Add(btn);
            return row;
        }).ToList();

        // 拦截记录（新→旧，VM 已保证环形保留）
        var records = _vm.RecentRecords;
        RecordsEmpty.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordList.ItemsSource = records.Select(r => (object)new TextBlock
        {
            Text = PopupPageViewModel.Describe(r),
            FontSize = 12.5,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoInk2Brush"],
        }).ToList();
    }
}
