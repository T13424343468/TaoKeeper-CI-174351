using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.AppKernel.ViewModels;

namespace TaoKeeper.App.Views;

/// <summary>工具结果行展示态（大文件/空间分析两个列表共用：路径 + 体积转述文本）。</summary>
public sealed class ToolItemRow
{
    public string PathText { get; }
    public string SizeText { get; }

    public ToolItemRow(string pathText, string sizeText) =>
        (PathText, SizeText) = (pathText, sizeText);
}

/// <summary>
/// 工具箱页 code-behind：8 张快捷卡为入口（原型 v19 布局与文案不动）。
/// 大文件查找点击展开结果区；空间分析/重复文件两卡点击整页进入专项页
///（两页不占独立路由，VM 经导航参数带上）；未扫描时 VM 汇总文本即「尚未扫描」（如实，不假数据）；
/// 其余工具卡点击如实提示随运行期接线启用。
/// 本页零写动作：所有清理建议只作展示，处置一律回到磁盘清理/专项页走确认闸门。
/// </summary>
public sealed partial class ToolsPage : Page
{
    private ToolboxPageViewModel _toolbox = new();
    private SpaceAnalysisPageViewModel _space = new();
    private DuplicateFilesPageViewModel _dup = new();

    public ToolsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ToolsPageBundle bundle)
        {
            Bind(bundle.Toolbox, bundle.Space, bundle.Duplicate);
        }
    }

    /// <summary>注入三个既有 VM（未注入的保持 new() 未扫描空态，如实显示）。</summary>
    public void Bind(ToolboxPageViewModel? toolbox = null,
                     SpaceAnalysisPageViewModel? space = null,
                     DuplicateFilesPageViewModel? duplicate = null)
    {
        _toolbox = toolbox ?? _toolbox;
        _space = space ?? _space;
        _dup = duplicate ?? _dup;
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;

        // 先收起其他结果区，只展开当前工具的（一次一屏，贴合原型单页节奏）
        LargeResult.Visibility = Visibility.Collapsed;
        SpaceResult.Visibility = Visibility.Collapsed;
        DupResult.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "large":
                LargeSummary.Text = _toolbox.SummaryText; // 未扫描即「尚未扫描」，VM 口径如实
                LargeItems.ItemsSource = _toolbox.Plan is { } plan
                    ? plan.LargeFiles
                        .Select(f => new ToolItemRow(f.FullPath, DiskCleanupPageViewModel.Fmt(f.SizeBytes)))
                        .ToArray()
                    : Array.Empty<ToolItemRow>();
                LargeResult.Visibility = Visibility.Visible;
                ToolStatus.Text = "大文件查找：仅呈现建议清单；处置请逐条确认，默认进回收站，可还原。";
                break;

            case "space":
                // 空间分析整页进入（VM 经导航参数带上，未扫描态如实展示，不假数据）
                Frame.Navigate(typeof(SpaceAnalysisPage), _space);
                break;

            case "dup":
                // 重复文件整页进入（VM 经导航参数带上，未扫描态如实展示，不假数据）
                Frame.Navigate(typeof(DuplicateFilesPage), _dup);
                break;

            default:
                // 相似图片/右键菜单/粉碎/进程/系统信息：引擎能力未接，如实说明，不假进度
                ToolStatus.Text = "「" + TagTitleOf(tag) + "」随运行期接线启用；当前版本未接入该扫描通道，如实显示，不预演结果。";
                break;
        }
    }

    private static string TagTitleOf(string tag) => tag switch
    {
        "similar" => "相似图片",
        "ctxmenu" => "右键菜单管理",
        "shred" => "文件粉碎",
        "proc" => "进程管理",
        "sysinfo" => "系统信息",
        _ => "该工具",
    };
}

/// <summary>工具箱页一次性注入包（导航参数载体，三个 VM 可全空=未扫描态）。</summary>
public sealed class ToolsPageBundle
{
    public ToolboxPageViewModel? Toolbox { get; init; }
    public SpaceAnalysisPageViewModel? Space { get; init; }
    public DuplicateFilesPageViewModel? Duplicate { get; init; }
}
