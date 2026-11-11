using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;

namespace TaoKeeper.App.Views;

/// <summary>
/// 启动项页 code-behind：数据经 StartupPageViewModel 注入（Bind），
/// 未接线时按未测态如实展示。只读骨架：清单、来源、风险与状态，无任何写动作。
/// </summary>
public sealed partial class StartupPage : Page
{
    private StartupPageViewModel _vm = new();

    /// <summary>启动项数据提供者（宿主运行期接线；未接线=null → 未测态如实展示，不假清单）。</summary>
    public static Func<StartupPageViewModel?>? ViewModelProvider { get; set; }

    public StartupPage()
    {
        InitializeComponent();
        Bind(new StartupPageViewModel());
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 主窗在导航参数里带 VM（运行期接线前为 null → 未测态兜底）
        if (e.Parameter is StartupPageViewModel vm) Bind(vm);
    }

    /// <summary>注入启动项清单（未扫描态经 VM 层保证如实，不假清单）。</summary>
    public void Bind(StartupPageViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;

        SummaryLine.Text = vm.SummaryText;
        EmptyLine.Visibility = vm.HasItems ? Visibility.Collapsed : Visibility.Visible;
        ItemList.Items.Clear();
        foreach (var item in vm.Items)
            ItemList.Items.Add(BuildRow(item));
    }

    private StackPanel BuildRow(StartupItemEntry item)
    {
        var riskText = StartupPageViewModel.RiskText(item);
        var riskBrush = riskText switch
        {
            "高风险" => ThemeBrush("TaoCinnabarBrush"),
            "未确认发布者" => ThemeBrush("TaoGoldBrush"),
            _ => ThemeBrush("TaoBambooBrush"),
        };

        return new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(0, 4, 0, 4),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = item.Name,
                            FontSize = 13.5,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = ThemeBrush("TaoInkBrush"),
                        },
                        new TextBlock
                        {
                            Text = StartupPageViewModel.StateText(item),
                            FontSize = 12,
                            Foreground = ThemeBrush("TaoInk3Brush"),
                            VerticalAlignment = VerticalAlignment.Bottom,
                        },
                        new TextBlock
                        {
                            Text = riskText,
                            FontSize = 12,
                            Foreground = riskBrush,
                            VerticalAlignment = VerticalAlignment.Bottom,
                        },
                    },
                },
                new TextBlock
                {
                    Text = StartupPageViewModel.DetailText(item),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TaoInk2Brush"),
                },
            },
        };
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
