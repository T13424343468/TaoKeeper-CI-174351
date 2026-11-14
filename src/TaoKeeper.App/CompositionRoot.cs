using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TaoKeeper.Adapters;
using TaoKeeper.AppKernel.Settings;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.App.Views;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.App;

/// <summary>
/// 组合根（接线唯一入口，App 启动时调用一次）：把壳的各页数据提供者接到真实引擎/适配器。
/// 各页视图模型壳生命周期内单例（扫描结果跨页保留，如实反映最近一次扫描态）；
/// 首启未扫描的页照旧走「尚未扫描」如实态——接线不等于造假数据。
/// </summary>
internal static class CompositionRoot
{
    private static DiskCleanupPageViewModel? _disk;
    private static SoftwarePageViewModel? _software;
    private static MemPageViewModel? _mem;
    private static SpeedupPageViewModel? _boost;
    private static DriverPageViewModel? _driver;
    private static PopupPageViewModel? _popup;
    private static StorageSensePageViewModel? _storage;
    private static StartupPageViewModel? _startup;
    private static WidgetPageViewModel? _widget;
    private static ToolboxPageViewModel? _toolbox;
    private static SpaceAnalysisPageViewModel? _space;
    private static DuplicateFilesPageViewModel? _duplicates;
    private static SimilarPhotoPageViewModel? _similar;

    /// <summary>启动项适配器（P4/P5 与 R-3 重扫共用同一实例与快照路径）。</summary>
    private static WindowsStartupControlAdapter? _startupAdapter;

    /// <summary>已装清单缓存（后台 winget 扫描回填；完成前 null=界面如实「尚未扫描」）。</summary>
    private static IReadOnlyList<SoftwareEntry>? _installedCache;

    /// <summary>最近一次真实内存快照的物理占用百分比（未取到快照为 null，总览按未知态显示）。</summary>
    private static int? _memPercent;

    /// <summary>最近一次真实内存快照（体检/加速/挂件共用；不可得为 null）。</summary>
    private static MemorySnapshot? _latestMemory;

    /// <summary>后台垃圾扫描缓存（体检/存储感知/挂件共用；未完成为 null）。</summary>
    private static JunkScanResult? _junkScanCache;

    /// <summary>后台启动项扫描缓存（体检启动项维度；未完成为 null）。</summary>
    private static IReadOnlyList<StartupItemEntry>? _startupItemsCache;

    /// <summary>体检上下文缓存（各维度就绪后重建；全空=未测态）。</summary>
    private static HealthContext _checkupContext = HealthContext.Empty;

    /// <summary>近 3 次开机耗时（毫秒，事件日志口径；不可读为空清单=未测态）。</summary>
    private static IReadOnlyList<long> _bootTimesMs = Array.Empty<long>();

    /// <summary>清理执行器（DiskCleanupPage 确认后调用；null=未接线，页面如实提示）。</summary>
    internal static Func<IReadOnlyList<CleanupActionItem>, Task<CleanupBatchResult>>? CleanupExecutor { get; private set; }

    internal static string LedgerPath => Path.Combine(DataDir, "cleanup-ledger.json");

    /// <summary>数据目录单一真源（设置/账本/快照共用；AppTheme 主题读取同口径，2026-11-14 起废弃 LocalState 旧口径）。</summary>
    internal static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoKeeper");

    internal static void Wire(MainWindow shell)
    {
        // R-P2-16：安装器勾选写入初始设置——首启（无设置文件）按安装戳落一次盘，之后以设置文件为准（设置页可改）
        TrySeedInstallerSettings();

        var startupAdapter = new WindowsStartupControlAdapter(Path.Combine(DataDir, "startup-snapshot.json"));
        _startupAdapter = startupAdapter; // R-3：重扫执行器共用同一适配器实例与快照路径
        var popupStore = new PopupConfigStore(DataDir);
        var storageStore = new StorageSenseStore(DataDir);

        _disk = new DiskCleanupPageViewModel();
        _software = new SoftwarePageViewModel();
        _mem = new MemPageViewModel();
        _boost = new SpeedupPageViewModel(new SpeedupCoordinator(startupAdapter)); // P5：真实注册表/启动文件夹适配器
        _driver = new DriverPageViewModel();
        _popup = new PopupPageViewModel(popupStore.LoadRules(), popupStore.LoadWhitelist()); // P8：持久化规则/白名单
        _popup.LoadRecords(popupStore.LoadRecords());
        _popup.ConfigChanged = () => PersistPopup(popupStore, _popup);
        _storage = new StorageSensePageViewModel(); // P9：默认关（引擎 Default），从盘上回放用户既有方案
        _storage.LoadPlan(storageStore.Load());
        _storage.PlanChanged = () => storageStore.Save(_storage.Plan);
        _widget = new WidgetPageViewModel();

        // 总览：体检未测态 + 当前各页真实读数（null=尚未扫描，总览按未测态如实显示）
        shell.HomeViewModelProvider = () => HomePageViewModel.Build(
            new CheckupPageViewModel().RunCheckup(_checkupContext), null,
            _disk.HasScan ? _disk.LastScan : null,
            _software.Plan is null ? null : _software,
            MemUsedPercent(),
            ledgerEntries: null);

        shell.BoostViewModelProvider = () => _boost;
        shell.MemViewModelProvider = () => _mem;
        shell.SwStoreViewModelProvider = () => _software;
        // R-P12-9 托盘设置需跨会话生效（自启/常驻），接线既有 AppSettingsStore（主题读取同走本文件，AppTheme 单口径已统一）
        shell.SettingsViewModelProvider = () => new SettingsPageViewModel(ResolveSettingsStore());
        shell.DiskViewModelProvider = () => _disk;
        shell.DriverViewModelProvider = () => _driver;
        shell.CheckupViewModelProvider = () => new CheckupPageViewModel().RunCheckup(_checkupContext); // P4：真实上下文
        shell.PopupViewModelProvider = () => _popup;
        shell.StorageViewModelProvider = () => _storage;
        shell.WidgetViewModelProvider = () => _widget;
        shell.ToolsBundleProvider = () => new ToolsPageBundle { Toolbox = _toolbox, Space = _space, Duplicate = _duplicates, Similar = _similar };

        // P10 相似图片：只读枚举用户图片目录（不存在则如实空清单，不假数据）
        SimilarPhotoPage.ScanRootsProvider = () =>
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return Directory.Exists(pictures) ? new[] { pictures } : Array.Empty<string>();
        };

        // P4/P5 启动项页：后台扫描完成后回填缓存 VM（未完成时如实「尚未扫描」）
        StartupPage.ViewModelProvider = () => _startup;

        // P8 弹窗页：真实窗口扫描 + 用户确认后的优雅关闭
        PopupPage.WindowScannerProvider = () => OperatingSystem.IsWindows() ? new PopupWindowScanner() : null;

        // P9 存储感知页：真实候选扫描 + 清理执行（与磁盘清理同一执行链，回收站语义）
        StoragePage.StorageCandidatesProvider = ScanStorageCandidates;
        StoragePage.CleanupExecutorProvider = items => CleanupExecutor!(items);

        // P10 工具箱：大文件扫描根（用户目录，只读）+ 真实系统读数
        ToolsPage.LargeScanRootsProvider = DefaultLargeScanRoots;

        // R-3 不可点页接线（均只读零写动作）：体检「开始体检」全链、空间分析页内即扫、启动项页内重扫
        CheckupPage.CheckupExecutor = RunCheckupAsync;
        SpaceAnalysisPage.ScanRootsProvider = DefaultLargeScanRoots;
        StartupPage.RescanProvider = RescanStartupAsync;

        // R-P10-4a~4b 文件粉碎：快照根落在数据目录（粉碎=显式确认后快照先落盘再覆写删除，
        // 红线 N1 由引擎收口；到期物理清除走 PurgeExpired，页面二次确认后调用）
        ToolsPage.ShredEngineProvider = () => new FileShredder(Path.Combine(DataDir, "shred-snapshots"));

        // P11 挂件：一键清理走与主程序同一执行链（同确认、同回收站语义、同清理账）
        WidgetPage.CleanupExecutorProvider = items => CleanupExecutor!(items);
        WidgetPage.JunkCandidatesProvider = JunkCandidatesForWidget;

        // R-P12-9 托盘：立即清理走同一执行链与候选缓存；自启注册器仅 Windows 接线（HKCU Run，非 MSIX 打包形态）
        TrayIconController.JunkCandidatesProvider = JunkCandidatesForWidget;
        TrayIconController.CleanupExecutorProvider = items => CleanupExecutor!(items);
        TrayIconController.AutostartRegistrar = OperatingSystem.IsWindows()
            ? new RegistryRunKeyAutostartRegistrar()
            : null;

        // 性能加速：真实进程快照（System.Diagnostics 枚举）；内存读数供加速计划判定
        _boost.ProcessSnapshotProvider = CaptureProcessSnapshot;
        BoostPage.CurrentMemoryProvider = () => _latestMemory;
        BoostPage.BootTimesProvider = () => _bootTimesMs; // P5-2：事件日志开机耗时口径

        // 内存管理：真实内存快照（GlobalMemoryStatusEx；读不到时调用方按未测态兜底）
        TryWireMemory(_mem);
        // P-D2 页面文件方案真执行：三步确认后的执行器接线（改前自动备份、快照可回滚；未确认绝不碰系统）
        MemPage.PageFileApplyExecutor = ApplyPageFilePlan;
        RefreshWidgetFrame(); // P11：内存就绪即出真实帧（垃圾/进程未就绪字段如实 null，挂件灰显该项）

        // 软件管家：后台 winget 已装清单缓存 + 页面钩子（缓存未就绪时返回 null，页面如实显示未扫描）
        KickOffInstalledScan();
        SoftwarePage.InstalledListProvider = () => _installedCache;

        // 驱动：WUA COM + pnputil 真实适配器
        DriverPage.DriverAdapterProvider = () => new WindowsDriverUpdateAdapter();

        // 清理执行：真实回收站移动 + 清理账落盘（原子写在 CleanupLedger 内）
        var ledger = new CleanupLedger(LedgerPath);
        var coordinator = new CleanupCoordinator(new Win32RecycleBinAdapter(), ledger);
        CleanupExecutor = items => coordinator.ExecuteAsync(items);

        // 后台真实扫描：启动项清单（体检+加速）、垃圾候选（体检+存储感知+挂件）、开机耗时读数
        KickOffStartupScan(startupAdapter);
        KickOffJunkScan();
        KickOffBootTimeRead();
    }

    private static int? MemUsedPercent() => _memPercent;

    /// <summary>R-P12-9：设置存储渠道（托盘自启/常驻需跨会话生效）。目录不可写时返回 null，页面如实标「仅本会话生效」。</summary>
    private static AppSettingsStore? ResolveSettingsStore()
    {
        try
        {
            return new AppSettingsStore(Path.Combine(DataDir, "settings.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null; // 无存储渠道时设置层自行如实降级（改动仅本会话生效），不假落盘
        }
    }

    /// <summary>
    /// R-P2-16 安装器勾选写入初始设置：仅首启（设置文件不存在）且安装戳存在时套用并落盘一次；
    /// 截键不存在（非安装器运行/旧版升级）保持代码默认值；落不了盘不挡启动（下次首启再补），不假成功。
    /// </summary>
    private static void TrySeedInstallerSettings()
    {
        try
        {
            var settingsPath = Path.Combine(DataDir, "settings.json");
            if (File.Exists(settingsPath)) return;

            var stamps = ReadInstallOptInStamps();
            if (stamps is null) return;

            var store = new AppSettingsStore(settingsPath);
            store.Save(InstallOptInDefaults.Apply(store.Load(), stamps));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 初始设置落不了盘不挡启动：保持代码默认值，与 ResolveSettingsStore 同降级口径
        }
    }

    /// <summary>读安装戳（MSI 恒写四值 0/1；截键不存在=null）。仅 Windows 有注册表语义，其余平台如实返回无戳。</summary>
    private static InstallOptInDefaults.InstallerOptIns? ReadInstallOptInStamps()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Tao\TaoKeeper\InstallOptIns");
            if (key is null) return null;

            bool? ReadStamp(string name) => key.GetValue(name) is int v ? v == 1 : null;
            return new InstallOptInDefaults.InstallerOptIns(
                ReadStamp("Autostart"), ReadStamp("ContextMenu"), ReadStamp("Widget"), ReadStamp("WeeklyCleanup"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or ArgumentException)
        {
            return null; // 注册表读不到按无戳如实处理，保持代码默认值
        }
    }

    /// <summary>
    /// P-D2：页面文件方案真执行（MemPage 三步确认后调用）。改前自动备份原 PagingFiles，写目标条目；
    /// custom 档位无输入界面时如实回执；权限不足/键不可读写如实上报，未做任何半截改动不假成功。
    /// </summary>
    private static string ApplyPageFilePlan(string planKey)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return "页面文件管理仅 Windows 真机可用。";
            var snapshot = _latestMemory ?? new Win32MemorySnapshotAdapter().Capture();
            var physicalBytes = snapshot.TotalPhysicalBytes;
            if (physicalBytes <= 0) return "物理内存读数不可用，未做任何改动。";
            var settings = PageFilePlanComposer.FromPlanKey(planKey);
            if (settings is null)
                return "自定义档位需要数值输入界面（本版未提供），请先使用智能推荐/性能优先/空间优先/系统托管。";

            var adapter = new Win32PageFileApplyAdapter();
            var current = adapter.ReadCurrentPagingFiles();
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:";
            var target = PageFilePlanComposer.ComposeTargetEntries(settings, physicalBytes, systemDrive);
            if (PageFilePlanComposer.SameAsCurrent(target, current))
                return $"当前已是该方案（{PageFilePlanComposer.Describe(current)}），无需改动。";

            adapter.BackupCurrent(current); // 先落盘快照再动系统：断电/崩溃后快照仍在，可回滚
            adapter.ApplyEntries(target);
            return $"已应用：{PageFilePlanComposer.Describe(current)} → {PageFilePlanComposer.Describe(target)}。"
                + "重启后生效；改前设置已备份，可随时一键回滚。";
        }
        catch (UnauthorizedAccessException)
        {
            return "权限不足（页面文件设置需要管理员权限），未做任何改动。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            return $"应用未完成：{ex.Message}";
        }
    }

    private static void TryWireMemory(MemPageViewModel mem)
    {
        try
        {
            var snapshot = new Win32MemorySnapshotAdapter().Capture();
            mem.ApplySnapshot(snapshot);
            _latestMemory = snapshot;
            _memPercent = (int)snapshot.PhysicalUsedPercent;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            // 快照不可得时保持「未测」态，不假数据（内存页空态如实展示）
        }
    }

    private static StartupProcessSnapshot? CaptureProcessSnapshot()
    {
        try
        {
            var processes = Process.GetProcesses()
                .Select(p =>
                {
                    try
                    {
                        return new ProcessEntry(p.Id, p.ProcessName, Publisher: null,
                            CpuPercent: 0, WorkingSetBytes: p.WorkingSet64, IsSigned: null);
                    }
                    finally { p.Dispose(); }
                })
                .OrderByDescending(x => x.WorkingSetBytes)
                .ToList();
            // CPU 占用率需两次采样差分，单次快照如实标 0（排行按内存序展示，不假 CPU 数）
            return new StartupProcessSnapshot(DateTimeOffset.UtcNow, Array.Empty<StartupItemEntry>(), processes);
        }
        catch (Exception)
        {
            return null; // 枚举失败按未测态，不假数据
        }
    }

    /// <summary>P11：聚合当前真实读数为挂件一帧（内存/进程取实时快照，垃圾用最近一次真实扫描）；
    /// 任一数据未就绪对应字段如实 null（挂件灰显该项），不编数。</summary>
    private static void RefreshWidgetFrame()
    {
        if (_widget is null) return;
        try
        {
            var processScan = CaptureProcessSnapshot();
            var frame = WidgetSnapshotAggregator.Build(
                DateTimeOffset.Now, _latestMemory, _junkScanCache, processScan);
            _widget.ApplyFrame(frame, DateTimeOffset.Now);
        }
        catch (Exception)
        {
            // 聚合失败保持当前帧（可能为 null=未就绪态），不假数据
        }
    }

    private static void KickOffInstalledScan()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var adapter = new WingetSoftwareSourceAdapter();
                _installedCache = await adapter.ListInstalledAsync();
            }
            catch (Exception)
            {
                // winget 不可得/失败保持缓存 null：页面如实「尚未扫描」，不假数据
            }
        });
    }

    /// <summary>P4/P5：后台真实启动项扫描（注册表 Run 键 + 启动文件夹），完成后重建体检上下文。</summary>
    private static void KickOffStartupScan(WindowsStartupControlAdapter adapter)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _startup = await RunStartupScanAsync(CancellationToken.None);
                RebuildCheckupContext();
            }
            catch (Exception)
            {
                // 扫描失败保持未测态：体检启动项维度如实缺席，不假数据
            }
        });
    }

    /// <summary>R-3：启动项只读扫描（供后台首扫与页内「重新扫描」共用；未接线适配器返回 null）。</summary>
    private static async Task<StartupPageViewModel?> RunStartupScanAsync(CancellationToken cancellation)
    {
        if (_startupAdapter is null) return null;
        _startupItemsCache = await _startupAdapter.ListStartupItemsAsync(cancellation);
        return await new StartupPageViewModel().LoadAsync(_startupAdapter, cancellation);
    }

    /// <summary>R-3：启动项页「重新扫描」执行器：只读重扫并同步体检上下文。</summary>
    private static async Task<StartupPageViewModel?> RescanStartupAsync(CancellationToken cancellation)
    {
        var vm = await RunStartupScanAsync(cancellation);
        if (vm is not null) RebuildCheckupContext();
        return vm;
    }

    /// <summary>P4：后台真实垃圾扫描（与磁盘清理页同一规则引擎与 %TEMP% 等真实目录），完成后重建体检上下文。</summary>
    private static void KickOffJunkScan()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _junkScanCache = await RunJunkScanAsync();
                RebuildCheckupContext();
                RefreshWidgetFrame(); // P11：垃圾概览就绪后补全挂件帧（真实字节数与项数）
            }
            catch (Exception)
            {
                // 扫描失败保持未测态：体检垃圾维度如实缺席
            }
        });
    }

    /// <summary>R-3：垃圾只读扫描（供后台首扫与体检「开始体检」共用；失败向上抛，调用方如实缺席）。</summary>
    private static async Task<JunkScanResult> RunJunkScanAsync()
    {
        var engine = new JunkRuleEngine();
        return await engine.ScanAsync(Environment.ExpandEnvironmentVariables, new HashSet<string>());
    }

    /// <summary>
    /// R-3：体检「开始体检」执行器（R-P4-1 全链数据侧）：真实各维只读扫描→汇总上下文。
    /// 任一维度失败如实缺席（该维保持 null=未测）；全部维度都未取得数据时报红不假完成。
    /// </summary>
    private static async Task<HealthContext> RunCheckupAsync(CancellationToken cancellation)
    {
        var gotAny = false;

        try
        {
            _junkScanCache = await RunJunkScanAsync();
            gotAny = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // 垃圾维度本轮未测，如实缺席（不假零）
        }

        try
        {
            var startup = await RunStartupScanAsync(cancellation);
            if (startup is not null) { _startup = startup; gotAny = true; }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // 启动项维度本轮未测，如实缺席
        }

        RebuildCheckupContext();
        if (!gotAny && _checkupContext == HealthContext.Empty)
            throw new InvalidOperationException("所有体检维度均未取得数据（扫描均失败），请稍后重试");
        return _checkupContext;
    }

    /// <summary>P5-2：开机耗时读数（事件日志口径，3 次取中位数；不可读=空清单未测态）。</summary>
    private static void KickOffBootTimeRead()
    {
        _ = Task.Run(() =>
        {
            try { _bootTimesMs = new BootTimeReader().ReadRecentBootTimesMs(3); }
            catch (Exception) { _bootTimesMs = Array.Empty<long>(); }
        });
    }

    /// <summary>P4：用各维度最新真实数据重建体检上下文（未就绪维度保持 null=未测，不填零冒充实测）。</summary>
    private static void RebuildCheckupContext()
    {
        long? freeBytes = null, totalBytes = null;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                freeBytes = drive.AvailableFreeSpace;
                totalBytes = drive.TotalSize;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Security.SecurityException)
        {
            // 系统盘定位失败：该维度本轮未测
        }

        _checkupContext = CheckupContextBuilder.Build(
            junkScan: _junkScanCache,
            startupItems: _startupItemsCache,
            systemDiskFreeBytes: freeBytes,
            systemDiskTotalBytes: totalBytes,
            storageSensePlan: _storage?.Plan);
    }

    /// <summary>P8：规则/白名单/记录落盘（VM 钩子触发；本层只转交存储）。</summary>
    private static void PersistPopup(PopupConfigStore store, PopupPageViewModel vm)
    {
        try
        {
            store.SaveRules(vm.Rules);
            store.SaveWhitelist(vm.Whitelist);
            store.SaveRecords(vm.RecentRecords);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 持久化失败不影响内存态判定；下次变更重试。诊断数据不出本机。
        }
    }

    /// <summary>P9：按当前勾选范围做真实候选扫描（临时文件→%TEMP% 实测；其余范围到点并入确认预览，本版以实测范围为准）。</summary>
    private static IReadOnlyList<CleanupActionItem> ScanStorageCandidates()
    {
        var scope = _storage?.Plan.Scope ?? StorageCleanupScope.None;
        var items = new List<CleanupActionItem>();
        if ((scope & StorageCleanupScope.TempFiles) != 0)
        {
            var junk = _junkScanCache;
            if (junk is null) return items; // 尚无扫描结果：候选如实为空，不编数
            items.AddRange(junk.Candidates
                .Where(c => c.Rule.Id == "system-temp")
                .Select(c => new CleanupActionItem(c.FullPath, c.SizeBytes, "storage-sense", Confirmed: false, c.Rule.Id)));
        }
        return items;
    }

    /// <summary>P11：挂件一键清理的候选（与体检/存储感知同一垃圾扫描缓存；未扫描为空清单，不编明细）。</summary>
    private static IReadOnlyList<CleanupActionItem> JunkCandidatesForWidget()
    {
        var junk = _junkScanCache;
        if (junk is null) return Array.Empty<CleanupActionItem>();
        return junk.Candidates
            .Select(c => new CleanupActionItem(c.FullPath, c.SizeBytes, "widget", Confirmed: false, c.Rule.Id))
            .ToList();
    }

    /// <summary>P10：大文件扫描根（当前用户常用目录，只读；不存在则跳过）。
    /// 注意：.NET 的 SpecialFolder 枚举没有 Downloads 项，下载目录按用户主目录下的 Downloads 定位。</summary>
    private static IReadOnlyList<string> DefaultLargeScanRoots()
    {
        var roots = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string?>
        {
            string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        foreach (var path in candidates)
        {
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                if (Directory.Exists(path)) roots.Add(path);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException)
            {
                // 目录定位失败：跳过该根
            }
        }
        return roots;
    }
}
