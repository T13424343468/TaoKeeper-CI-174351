using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.AppKernel.ViewModels;

namespace TaoKeeper.App.Views;

/// <summary>
/// 空间分析页 code-behind：目录 Top-N 经 SpaceAnalysisPageViewModel 绑定；
/// 体积分层色与 VM.TierColorOf 同口径（≥1 GB 朱砂、≥100 MB 金、其余竹青，画刷随主题即时取值）；
/// 路径重建失败显示裸名并如实标注；便携降级模式如实说明。本页零写动作。
/// </summary>
public sealed partial class SpaceAnalysisPage : Page
{
    private SpaceAnalysisPageViewModel? _vm;

    public SpaceAnalysisPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SpaceAnalysisPageViewModel vm) Bind(vm);
    }

    /// <summary>注入空间分析展示态（未传时保持未扫描空态，不假数据）。</summary>
    public void Bind(SpaceAnalysisPageViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        Refresh();
    }

    private void Refresh()
    {
        if (_vm is null) return;

        SummaryText.Text = _vm.SummaryText;
        PortableNote.Visibility = _vm.PortableMode ? Visibility.Visible : Visibility.Collapsed;

        // 孤儿体积：父链缺失/成环部分，如实入汇总不入清单
        if (_vm.OrphanSize > 0)
        {
            OrphanNote.Text = $"另有未归档体积 {Fmt(unchecked((long)_vm.OrphanSize))}"
                + (_vm.OrphanFiles > 0 ? $"（{_vm.OrphanFiles} 项）" : string.Empty)
                + "（父链缺失或成环，不进目录清单）。";
            OrphanNote.Visibility = Visibility.Visible;
        }
        else
        {
            OrphanNote.Visibility = Visibility.Collapsed;
        }

        var rows = _vm.TopDirectories;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DirRows.ItemsSource = rows.Select(MakeRow).ToList();

        HiddenNote.Text = _vm.HiddenDirectoryCount > 0
            ? $"其余 {_vm.HiddenDirectoryCount} 项未列出（只显示 Top {rows.Count}）。"
            : string.Empty;
        HiddenNote.Visibility = _vm.HiddenDirectoryCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement MakeRow(SpaceAnalysisItem item)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 体积分层色点（与 VM.TierColorOf 同口径，画刷随三主题即时取值）
        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            Background = TierBrush(item.RecursiveSize),
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = item.PathText,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
        });
        if (item.PathFallback)
        {
            info.Children.Add(new TextBlock
            {
                Text = "路径重建失败，显示裸名（不编造完整路径）。",
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TaoGoldBrush"],
            });
        }
        info.Children.Add(new TextBlock
        {
            Text = $"{item.FileCount} 个文件",
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["TaoInk3Brush"],
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var size = new TextBlock
        {
            Text = item.SizeText,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TaoAzureDeepBrush"],
        };
        Grid.SetColumn(size, 2);
        grid.Children.Add(size);
        return grid;
    }

    private Brush TierBrush(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (Brush)Application.Current.Resources["TaoCinnabarBrush"],
        >= 100L * 1024 * 1024 => (Brush)Application.Current.Resources["TaoGoldBrush"],
        _ => (Brush)Application.Current.Resources["TaoBambooBrush"],
    };

    private static string Fmt(long bytes)
    {
        double v = bytes;
        const long GB = 1024L * 1024 * 1024, MB = 1024 * 1024, KB = 1024;
        return bytes >= GB ? $"{v / GB:F1} GB"
             : bytes >= MB ? $"{v / MB:F0} MB"
             : bytes >= KB ? $"{v / KB:F0} KB"
             : $"{bytes} B";
    }
}
