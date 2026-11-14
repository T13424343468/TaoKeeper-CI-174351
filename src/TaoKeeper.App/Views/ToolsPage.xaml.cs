using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.Adapters;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Modules.Toolbox;

namespace TaoKeeper.App.Views;

/// <summary>工具结果行展示态（大文件/空间分析/粉碎列表共用：路径 + 体积转述文本）。</summary>
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
/// 系统信息=真实系统/内存读数；空间分析/重复文件两卡整页进入专项页；
/// 文件粉碎=显式确认后逐份「快照先落盘→覆写→删除」（R-P10-4a~4b，红线 N1 由引擎收口）。
/// 相似图片/右键菜单两通道本版未接引擎，如实降级说明（不预演结果）。
/// 本页除粉碎外零写动作：粉碎前逐项确认，快照 7 天内可还原，到期清除须二次确认。
/// </summary>
public sealed partial class ToolsPage : Page
{
    private ToolboxPageViewModel _toolbox = new();
    private SpaceAnalysisPageViewModel _space = new();
    private DuplicateFilesPageViewModel _dup = new();
    private SimilarPhotoPageViewModel _similar = new();
    private FileShredPageViewModel _shred = new();

    /// <summary>大文件扫描根（宿主接线；用户常用目录，只读）。</summary>
    public static Func<IReadOnlyList<string>>? LargeScanRootsProvider { get; set; }

    /// <summary>粉碎引擎提供者（宿主接线；快照根落在数据目录。null=未接线，页面如实提示）。</summary>
    public static Func<FileShredder?>? ShredEngineProvider { get; set; }

    /// <summary>系统关键目录（N6：选择器与计划共同口径，逐段前缀判定由引擎负责）。</summary>
    private static IReadOnlyList<string> CriticalRoots { get; } = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
    }.Where(p => !string.IsNullOrEmpty(p)).ToList();

    private bool _largeScanRunning;
    private List<string>? _shredPaths;
    private CancellationTokenSource? _shredCts;
    private bool _shredRunning;
    private bool _shredHasAllowed;

    public ToolsPage()
    {
        InitializeComponent();
        _shred.PhaseChanged = _ => RefreshShredState();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ToolsPageBundle bundle)
        {
            Bind(bundle.Toolbox, bundle.Space, bundle.Duplicate, bundle.Similar);
        }
    }

    /// <summary>注入既有 VM（未注入的保持 new() 未扫描空态，如实显示）。</summary>
    public void Bind(ToolboxPageViewModel? toolbox = null,
                     SpaceAnalysisPageViewModel? space = null,
                     DuplicateFilesPageViewModel? duplicate = null,
                     SimilarPhotoPageViewModel? similar = null)
    {
        _toolbox = toolbox ?? _toolbox;
        _space = space ?? _space;
        _dup = duplicate ?? _dup;
        _similar = similar ?? _similar;
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;

        // 先收起其他结果区，只展开当前工具的（一次一屏，贴合原型单页节奏）
        LargeResult.Visibility = Visibility.Collapsed;
        SpaceResult.Visibility = Visibility.Collapsed;
        DupResult.Visibility = Visibility.Collapsed;
        ShredResult.Visibility = Visibility.Collapsed;
        CtxResult.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "ctxmenu":
                RunContextMenuScan();
                break;
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

            case "similar":
                // R-P10-2b~2c：相似图片整页进入（页内即扫：只读枚举图片目录→感知哈希分组）
                Frame.Navigate(typeof(SimilarPhotoPage), _similar);
                break;

            case "recover":
                // R-P10-5：数据找回整页进入（误删恢复卡；镜像先行、源盘零写入，确认后写入）
                Frame.Navigate(typeof(DiskHardcorePage));
                break;

            case "shred":
                // R-P10-4a~4b：文件粉碎面板（选择→确认→粉碎→回执→快照还原/到期清除）
                EnterShredPanel();
                break;

            case "proc":
                RunProcessList();
                break;

            case "sysinfo":
                RunSystemInfo();
                break;
        }
        // 全部工具卡均已接真实通道或整页跳转，无剩余占位分支（V-A2：占位文案清零）。
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

    // ---------- R-P10-6 右键菜单管理 ----------

    /// <summary>右键菜单管理通道（宿主接线；null=未接线，页面如实提示）。</summary>
    public static Func<IContextMenuManager?>? ContextMenuManagerProvider { get; set; }

    /// <summary>只读扫描右键菜单条目（真实注册表读数），逐条给禁用/启用按钮（可逆，键改名不删数据）。</summary>
    private void RunContextMenuScan()
    {
        CtxResult.Visibility = Visibility.Visible;
        var mgr = ContextMenuManagerProvider?.Invoke();
        if (mgr is null)
        {
            CtxList.Items.Clear();
            CtxList.Items.Add(new TextBlock { FontSize = 12, Text = "右键菜单通道未就绪（非 Windows 环境不提供），不假数据。" });
            ToolStatus.Text = "右键菜单管理：通道未就绪，如实显示。";
            return;
        }

        try
        {
            CtxList.Items.Clear();
            var entries = mgr.Scan();
            if (entries.Count == 0)
            {
                CtxList.Items.Add(new TextBlock { FontSize = 12, Text = "没有扫到右键菜单条目（如实显示，不编数据）。" });
                return;
            }
            foreach (var e in entries)
            {
                var row = new StackPanel { Spacing = 2, Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = $"{(e.Enabled ? "●" : "○")} {e.Name}｜{e.LocationLabel}{(string.IsNullOrEmpty(e.DllPath) ? "" : $"｜{e.DllPath}")}",
                });
                var toggle = new Button
                {
                    Content = e.Enabled ? "禁用" : "启用",
                    Tag = e,
                    MinWidth = 64,
                    Margin = new Thickness(8, 0, 0, 0),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 4, 10, 4),
                };
                toggle.Click += OnCtxToggle;
                row.Children.Add(toggle);
                CtxList.Items.Add(row);
            }
            ToolStatus.Text = $"右键菜单管理：共 {entries.Count} 条（真实读数）。";
        }
        catch (Exception ex)
        {
            ToolStatus.Text = $"右键菜单扫描未完成：{ex.Message}";
        }
    }

    /// <summary>禁用/启用（可逆改名；禁用前二次确认，启用同样确认；失败如实提示不假成功）。</summary>
    private async void OnCtxToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ContextMenuEntry entry }) return;
        var mgr = ContextMenuManagerProvider?.Invoke();
        if (mgr is null) return;

        var dialog = new ContentDialog
        {
            Title = entry.Enabled ? $"确认禁用「{entry.Name}」？" : $"确认启用「{entry.Name}」？",
            Content = "禁用=键改名不删除，随时可在本页「启用」还原。",
            PrimaryButtonText = entry.Enabled ? "禁用" : "启用",
            CloseButtonText = "再想想",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var ok = entry.Enabled ? mgr.Disable(entry.KeyPath) : mgr.Enable(entry.KeyPath);
            ToolStatus.Text = ok
                ? $"{(entry.Enabled ? "禁用" : "启用")}完成：{entry.Name}"
                : $"{(entry.Enabled ? "禁用" : "启用")}未生效：{entry.Name}（如实报告，未做任何改动）";
        }
        catch (Exception ex)
        {
            ToolStatus.Text = $"操作未完成：{ex.Message}（未假报成功）";
        }
        RunContextMenuScan();
    }

    // ---------- R-P10-4a~4b 文件粉碎 ----------

    /// <summary>进入粉碎面板：未接线如实提示（不假面板）；已接线的展示快照清单与空计划态。</summary>
    private void EnterShredPanel()
    {
        ShredResult.Visibility = Visibility.Visible;
        var engine = ShredEngineProvider?.Invoke();
        if (engine is null)
        {
            ShredSummary.Text = "粉碎通道尚未接线，本版未执行任何粉碎。";
            ShredItems.ItemsSource = Array.Empty<ToolItemRow>();
            ToolStatus.Text = "文件粉碎：通道未接线，如实显示。";
            return;
        }
        RefreshShredSnapshotList(engine);
        ToolStatus.Text = _shredPaths is null
            ? "文件粉碎：点「选择文件…」挑选要粉碎的文件（可多选），粉碎前会逐项确认。"
            : "文件粉碎：文件已选好，点「开始粉碎」继续（会再次逐项确认）。";
    }

    /// <summary>选择文件（多选）→ 建计划并逐项展示（不可选项如实说明，N6 关键目录不可选）。</summary>
    private async void OnShredPick(object sender, RoutedEventArgs e)
    {
        if (_shredRunning) return;
        try
        {
            var shell = (Application.Current as App)?.Shell;
            if (shell is null)
            {
                ToolStatus.Text = "窗口尚未就绪，无法打开文件选择器。";
                return;
            }
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
                FileTypeFilter = { "*" },
            };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(shell));
            var picked = await picker.PickMultipleFilesAsync();
            if (picked is null || picked.Count == 0) return;

            _shredPaths = picked.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            RenderShredPlan();
        }
        catch (Exception ex)
        {
            ToolStatus.Text = $"文件选择未完成：{ex.Message}";
        }
    }

    /// <summary>按当前所选路径建计划：汇总文案 + 逐项行（允许=体积；拒绝=原因）。</summary>
    private void RenderShredPlan()
    {
        var engine = ShredEngineProvider?.Invoke();
        if (engine is null || _shredPaths is null) return;

        var plan = engine.BuildPlan(_shredPaths, CriticalRoots);
        _shredHasAllowed = plan.Any(i => i.Allowed);
        _shred.BeginPlan(plan);
        ShredItems.ItemsSource = plan
            .Select(i => new ToolItemRow(i.FullPath, i.Allowed ? DiskCleanupPageViewModel.Fmt(i.SizeBytes) : $"不可选 · {i.DeniedReason}"))
            .ToArray();
        ShredRunButton.IsEnabled = _shredHasAllowed;
    }

    /// <summary>开始粉碎：显式确认（逐项路径+大小+7 天还原说明）→ 执行 → 回执弹窗（个数+释放空间实测）。</summary>
    private async void OnShredRun(object sender, RoutedEventArgs e)
    {
        if (_shredRunning || _shredPaths is null) return;
        var engine = ShredEngineProvider?.Invoke();
        if (engine is null)
        {
            ToolStatus.Text = "粉碎通道尚未接线，本次未执行任何粉碎。";
            return;
        }

        var plan = engine.BuildPlan(_shredPaths, CriticalRoots);
        if (!plan.Any(i => i.Allowed))
        {
            ToolStatus.Text = "没有可粉碎的文件（不可选项已逐项说明），未执行任何粉碎。";
            return;
        }

        // R-P10-4a 显式确认：逐项列出路径+大小，并说明 7 天快照还原与不可选项
        var confirm = new ContentDialog
        {
            Title = "确认粉碎以下文件？",
            Content = BuildShredConfirmContent(plan),
            PrimaryButtonText = "开始粉碎",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        _shredRunning = true;
        _shredCts = new CancellationTokenSource();
        RefreshShredState();
        ToolStatus.Text = "粉碎中…逐份先落快照再覆写删除；取消则已碎保持已碎、未处理原状。";
        await _shred.RunAsync(async ct => await engine.ShredAsync(plan,
            new Progress<(int Index, string FullPath)>(p => ToolStatus.Text =
                $"粉碎中…已完成 {p.Index + 1}/{plan.Count}：{p.FullPath}"), ct), _shredCts.Token);
        _shredRunning = false;
        RefreshShredState();

        // R-P10-4b 完成回执：个数+释放空间（引擎实测一致）；失败/取消原因在状态行如实展示
        if (_shred.RunPhase == FileShredRunPhase.Completed)
        {
            RefreshShredSnapshotList(engine);
            var receipt = new ContentDialog
            {
                Title = "粉碎完成",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"已粉碎 {_shred.ShreddedCount} 个文件，释放 {DiskCleanupPageViewModel.Fmt(_shred.FreedBytes)}。\n7 天内可在本页「从快照还原…」恢复；到期物理清除前会再次确认。",
                },
                CloseButtonText = "知道了",
                XamlRoot = XamlRoot,
            };
            await receipt.ShowAsync();
        }
        else if (_shred.FailureMessage is { } reason)
        {
            ToolStatus.Text = reason;
        }
    }

    /// <summary>确认弹窗内容：逐项路径+大小 + 7 天还原说明（R-P10-4a 口径）。</summary>
    private static StackPanel BuildShredConfirmContent(IReadOnlyList<ShredItem> plan)
    {
        var list = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Text = string.Join("\n", plan.Select(i => i.Allowed
                ? $"{i.FullPath}（{DiskCleanupPageViewModel.Fmt(i.SizeBytes)}）"
                : $"{i.FullPath}（不可选：{i.DeniedReason}）")),
            MaxHeight = 220,
        };
        return new StackPanel { Spacing = 8, Children =
        {
            list,
            new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Text = $"粉碎后 7 天内可从快照还原（保留期默认 7 天）；快照未落盘成功的文件不会被粉碎。共 {FileShredPageViewModel.BuildPlanSummary(plan)}。",
            },
        } };
    }

    private void OnShredCancel(object sender, RoutedEventArgs e) => _shredCts?.Cancel();

    /// <summary>快照还原：列出全部快照，选一条还原到原路径（快照保留，可再还原）。</summary>
    private async void OnShredRestore(object sender, RoutedEventArgs e)
    {
        if (_shredRunning) return;
        var engine = ShredEngineProvider?.Invoke();
        if (engine is null) { ToolStatus.Text = "粉碎通道尚未接线，无法还原。"; return; }

        var snapshots = engine.ListSnapshots();
        if (snapshots.Count == 0) { ToolStatus.Text = "暂无快照：还没有可还原的粉碎记录。"; return; }

        var combo = new ComboBox { Header = "选择快照", Width = 460, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var s in snapshots)
            combo.Items.Add($"{s.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {DiskCleanupPageViewModel.Fmt(s.SizeBytes)} · {s.OriginalPath}");
        combo.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "从快照还原",
            Content = new StackPanel { Spacing = 8, Children =
            {
                combo,
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Text = "还原会把文件内容回拷到原路径（原路径目录不在则先建）；快照保留，可再次还原。",
                },
            } },
            PrimaryButtonText = "还原",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var target = snapshots[combo.SelectedIndex];
            engine.Restore(target.SnapshotId);
            ToolStatus.Text = $"已还原：{target.OriginalPath}（快照保留，可再次还原）。";
            RefreshShredState();
        }
        catch (Exception ex)
        {
            ToolStatus.Text = $"还原未完成：{ex.Message}";
        }
    }

    /// <summary>到期物理清除（全系统唯一合法永久清除路径）：二次确认后清除已过保留期的快照。</summary>
    private async void OnShredPurge(object sender, RoutedEventArgs e)
    {
        if (_shredRunning) return;
        var engine = ShredEngineProvider?.Invoke();
        if (engine is null) { ToolStatus.Text = "粉碎通道尚未接线，无法清除快照。"; return; }

        var expired = engine.ListExpired(DateTimeOffset.UtcNow);
        if (expired.Count == 0) { ToolStatus.Text = "没有已到保留期的快照；未做任何清除。"; return; }

        // 到期清除=不可逆，逐条列出后二次确认（R-P10-4b）
        var dialog = new ContentDialog
        {
            Title = "确认清除到期快照？",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 220,
                FontSize = 12,
                Text = $"以下 {expired.Count} 份快照已过保留期（{expired[0].RetentionDays} 天），清除后不可再还原：\n\n" +
                       string.Join("\n", expired.Select(s => $"{s.OriginalPath}（{DiskCleanupPageViewModel.Fmt(s.SizeBytes)}）")),
            },
            PrimaryButtonText = "永久清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var purged = engine.PurgeExpired(DateTimeOffset.UtcNow);
            ToolStatus.Text = $"已永久清除 {purged} 份到期快照（payload 与元数据一并清除）。";
            RefreshShredState();
        }
        catch (Exception ex)
        {
            ToolStatus.Text = $"快照清除未完成：{ex.Message}";
        }
    }

    /// <summary>刷新粉碎面板状态行与按钮可用性（PhaseChanged 回调接线）。</summary>
    private void RefreshShredState()
    {
        var running = _shred.RunPhase == FileShredRunPhase.Running;
        ShredProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ShredRunButton.IsEnabled = !running && _shredHasAllowed;
        ShredCancelButton.IsEnabled = running;
        ShredPickButton.IsEnabled = !running;
        ShredRestoreButton.IsEnabled = !running;
        ShredPurgeButton.IsEnabled = !running;
        ShredSummary.Text = running
            ? "粉碎进行中…（逐份快照先落盘，再覆写删除）"
            : _shred.RunPhase == FileShredRunPhase.Completed ? _shred.ReceiptText : _shred.PlanSummaryText;
    }

    /// <summary>刷新快照清单（保留期内的粉碎记录，倒序）。</summary>
    private void RefreshShredSnapshotList(FileShredder engine)
    {
        var snapshots = engine.ListSnapshots();
        ShredItems.ItemsSource = snapshots
            .Select(s => new ToolItemRow(s.OriginalPath,
                $"{DiskCleanupPageViewModel.Fmt(s.SizeBytes)} · {s.CreatedUtc.ToLocalTime():MM-dd HH:mm} · {s.RetentionDays} 天内可还原"))
            .ToArray();
        if (_shredPaths is null)
            ShredSummary.Text = snapshots.Count == 0
                ? "尚未选择文件；快照清单为空。"
                : $"共 {snapshots.Count} 份快照（见列表），7 天内可还原。";
    }
}

/// <summary>工具箱页一次性注入包（导航参数载体，三个 VM 可全空=未扫描态）。</summary>
public sealed class ToolsPageBundle
{
    public ToolboxPageViewModel? Toolbox { get; init; }
    public SpaceAnalysisPageViewModel? Space { get; init; }
    public DuplicateFilesPageViewModel? Duplicate { get; init; }
    public SimilarPhotoPageViewModel? Similar { get; init; }
}
