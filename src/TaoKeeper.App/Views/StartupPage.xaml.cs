using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;

namespace TaoKeeper.App.Views;

/// <summary>
/// 启动项页 code-behind：数据经 StartupPageViewModel 注入（Bind），
/// 未接线时按未测态如实展示。只读骨架：清单、来源、风险与状态，无任何写动作。
/// R-3 接线：页内「重新扫描」→ 只读清单（RescanProvider 经组合根接真实适配器），失败/未接线如实呈现。
/// </summary>
public sealed partial class StartupPage : Page
{
    private StartupPageViewModel _vm = new();

    /// <summary>启动项数据提供者（宿主运行期接线；未接线=null → 未测态如实展示，不假清单）。</summary>
    public static Func<StartupPageViewModel?>? ViewModelProvider { get; set; }

    /// <summary>R-3 重新扫描执行器（组合根接线：只读 ListStartupItemsAsync；未接线=null → 页面如实提示）。</summary>
    public static Func<CancellationToken, Task<StartupPageViewModel?>>? RescanProvider { get; set; }

    private CancellationTokenSource? _scanCts;
    private bool _scanRunning;

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

    /// <summary>R-3：重新扫描（只读；进行中防重入，失败如实不假清单）。</summary>
    private async void OnRescan(object sender, RoutedEventArgs e)
    {
        if (_scanRunning) return;
        var provider = RescanProvider;
        if (provider is null)
        {
            ShowStatus("扫描通道尚未接线，本次未执行任何扫描。");
            return;
        }

        _scanRunning = true;
        RescanButton.IsEnabled = false;
        ScanRing.Visibility = Visibility.Visible;
        try
        {
            _scanCts = new CancellationTokenSource();
            var vm = await provider(_scanCts.Token);
            if (vm is null) ShowStatus("扫描未返回清单（保持原清单），未做任何改动。");
            else { Bind(vm); ShowStatus(string.Empty); }
        }
        catch (OperationCanceledException)
        {
            ShowStatus("扫描已取消：保持原清单，未做任何改动。");
        }
        catch (Exception ex)
        {
            ShowStatus($"扫描未完成：{ex.Message}（只读扫描，未做任何改动）");
        }
        finally
        {
            _scanRunning = false;
            RescanButton.IsEnabled = true;
            ScanRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowStatus(string message)
    {
        ScanStatus.Text = message;
        ScanStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
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
