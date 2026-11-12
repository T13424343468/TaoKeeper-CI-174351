using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;

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
/// 真实通道：大文件查找=PortableDiskScanner 只读实扫用户目录；进程管理=真实进程枚举（只读快照）；
/// 系统信息=真实系统/内存读数；空间分析/重复文件两卡整页进入专项页。
/// 相似图片/右键菜单/文件粉碎三通道本版未接引擎，如实降级说明（不预演结果）。
/// 本页零写动作：所有清理建议只作展示，处置一律回到磁盘清理/专项页走确认闸门。
/// </summary>
public sealed partial class ToolsPage : Page
{
    private ToolboxPageViewModel _toolbox = new();
    private SpaceAnalysisPageViewModel _space = new();
    private DuplicateFilesPageViewModel _dup = new();

    /// <summary>大文件扫描根（宿主接线；用户常用目录，只读）。</summary>
    public static Func<IReadOnlyList<string>>? LargeScanRootsProvider { get; set; }

    private bool _largeScanRunning;

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
                _ = RunLargeFileScanAsync();
                break;

            case "space":
                // 空间分析整页进入（VM 经导航参数带上，未扫描态如实展示，不假数据）
                Frame.Navigate(typeof(SpaceAnalysisPage), _space);
                break;

            case "dup":
                // 重复文件整页进入（VM 经导航参数带上，未扫描态如实展示，不假数据）
                Frame.Navigate(typeof(DuplicateFilesPage), _dup);
                break;

            case "proc":
                RunProcessList();
                break;

            case "sysinfo":
                RunSystemInfo();
                break;

            default:
                // 相似图片/右键菜单/粉碎：引擎能力本版未接，如实说明，不假进度
                ToolStatus.Text = "「" + TagTitleOf(tag) + "」本版未接入该扫描通道，如实显示，不预演结果。";
                break;
        }
    }

    /// <summary>P10 大文件查找：PortableDiskScanner 只读实扫用户目录（真实路径+字节数，不预演清理）。</summary>
    private async Task RunLargeFileScanAsync()
    {
        if (_largeScanRunning) return;
        _largeScanRunning = true;
        try
        {
            ToolStatus.Text = "扫描中…（只读枚举用户目录，不动任何文件）";
            LargeItems.ItemsSource = Array.Empty<ToolItemRow>();
            var roots = LargeScanRootsProvider?.Invoke() ?? Array.Empty<string>();
            if (roots.Count == 0)
            {
                ToolStatus.Text = "未配置可扫描目录（目录不存在）；未做任何扫描。";
                return;
            }

            var engine = new PortableDiskScanner();
            var scan = await engine.ScanAsync(roots, new ScanOptions());
            _toolbox.BuildFromScan(scan, ToolboxOptions.Defaults());
            LargeSummary.Text = _toolbox.SummaryText;
            LargeItems.ItemsSource = _toolbox.Plan is { } plan
                ? plan.LargeFiles
                    .Select(f => new ToolItemRow(f.FullPath, DiskCleanupPageViewModel.Fmt(f.SizeBytes)))
                    .ToArray()
                : Array.Empty<ToolItemRow>();
            LargeResult.Visibility = Visibility.Visible;
            ToolStatus.Text = $"实扫完成：{scan.TotalFiles} 个文件（只读）；处置请回磁盘清理页走确认闸门。";
        }
        catch (Exception ex)
        {
            ToolStatus.Text = $"扫描未完成：{ex.Message}（未动任何文件）";
        }
        finally
        {
            _largeScanRunning = false;
        }
    }

    /// <summary>P10 进程管理：真实进程枚举只读快照（名称+内存占用），处置不代做。</summary>
    private void RunProcessList()
    {
        try
        {
            var rows = Process.GetProcesses()
                .Select(p =>
                {
                    try { return (Name: p.ProcessName, Bytes: p.WorkingSet64); }
                    finally { p.Dispose(); }
                })
                .OrderByDescending(x => x.Bytes)
                .Take(15)
                .Select(x => new ToolItemRow(x.Name, $"{x.Bytes / 1024.0 / 1024:F1} MB（内存占用，只读快照）"))
                .ToList();
            LargeSummary.Text = "真实进程快照（Top 15，按内存排序）";
            LargeItems.ItemsSource = rows;
            LargeResult.Visibility = Visibility.Visible;
            ToolStatus.Text = "结束进程属写动作，请用系统任务管理器，本页不代做。";
        }
        catch (Exception ex)
        {
            ToolStatus.Text = $"进程枚举未完成：{ex.Message}";
        }
    }

    /// <summary>P10 系统信息：真实系统/内存读数（GlobalMemoryStatusEx + 环境信息）。</summary>
    private void RunSystemInfo()
    {
        try
        {
            var snapshot = new Win32MemorySnapshotAdapter().Capture();
            var rows = new List<ToolItemRow>
            {
                new("操作系统", Environment.OSVersion.VersionString),
                new("处理器逻辑核心数", Environment.ProcessorCount + " 核"),
                new("物理内存总量", $"{snapshot.TotalPhysicalBytes / 1024.0 / 1024 / 1024:F1} GB"),
                new("物理内存可用", $"{snapshot.AvailablePhysicalBytes / 1024.0 / 1024 / 1024:F1} GB（占用 {snapshot.PhysicalUsedPercent:F0}%）"),
                new("系统盘", Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"),
            };
            LargeSummary.Text = "真实系统读数（本机，不出本机）";
            LargeItems.ItemsSource = rows;
            LargeResult.Visibility = Visibility.Visible;
            ToolStatus.Text = "详细空间分布请进「磁盘空间分析」。";
        }
        catch (Exception ex)
        {
            ToolStatus.Text = $"系统读数未完成：{ex.Message}（不假造读数）";
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
