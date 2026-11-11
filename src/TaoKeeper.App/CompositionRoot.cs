using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TaoKeeper.Adapters;
using TaoKeeper.AppKernel.ViewModels;
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

    /// <summary>已装清单缓存（后台 winget 扫描回填；完成前 null=界面如实「尚未扫描」）。</summary>
    private static IReadOnlyList<SoftwareEntry>? _installedCache;

    /// <summary>最近一次真实内存快照的物理占用百分比（未取到快照为 null，总览按未知态显示）。</summary>
    private static int? _memPercent;

    /// <summary>清理执行器（DiskCleanupPage 确认后调用；null=未接线，页面如实提示）。</summary>
    internal static Func<IReadOnlyList<CleanupActionItem>, Task<CleanupBatchResult>>? CleanupExecutor { get; private set; }

    private static string LedgerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoKeeper", "cleanup-ledger.json");

    internal static void Wire(MainWindow shell)
    {
        _disk = new DiskCleanupPageViewModel();
        _software = new SoftwarePageViewModel();
        _mem = new MemPageViewModel();
        _boost = new SpeedupPageViewModel();
        _driver = new DriverPageViewModel();

        // 总览：体检未测态 + 当前各页真实读数（null=尚未扫描，总览按未测态如实显示）
        shell.HomeViewModelProvider = () => HomePageViewModel.Build(
            new CheckupPageViewModel().RunCheckup(HealthContext.Empty), null,
            _disk.HasScan ? _disk.LastScan : null,
            _software.Plan is null ? null : _software,
            MemUsedPercent(),
            ledgerEntries: null);

        shell.BoostViewModelProvider = () => _boost;
        shell.MemViewModelProvider = () => _mem;
        shell.SwStoreViewModelProvider = () => _software;
        shell.SettingsViewModelProvider = () => new SettingsPageViewModel();
        shell.DiskViewModelProvider = () => _disk;
        shell.DriverViewModelProvider = () => _driver;

        // 性能加速：真实进程快照（System.Diagnostics 枚举；启动项清册暂未接线，如实空）
        _boost.ProcessSnapshotProvider = CaptureProcessSnapshot;

        // 内存管理：真实内存快照（GlobalMemoryStatusEx；读不到时调用方按未测态兜底）
        TryWireMemory(_mem);

        // 软件管家：后台 winget 已装清单缓存 + 页面钩子（缓存未就绪时返回 null，页面如实显示未扫描）
        KickOffInstalledScan();
        SoftwarePage.InstalledListProvider = () => _installedCache;

        // 驱动：WUA COM + pnputil 真实适配器
        DriverPage.DriverAdapterProvider = () => new WindowsDriverUpdateAdapter();

        // 清理执行：真实回收站移动 + 清理账落盘（原子写在 CleanupLedger 内）
        var ledger = new CleanupLedger(LedgerPath);
        var coordinator = new CleanupCoordinator(new Win32RecycleBinAdapter(), ledger);
        CleanupExecutor = items => coordinator.ExecuteAsync(items);
    }

    private static int? MemUsedPercent() => _memPercent;

    private static void TryWireMemory(MemPageViewModel mem)
    {
        try
        {
            var snapshot = new Win32MemorySnapshotAdapter().Capture();
            mem.ApplySnapshot(snapshot);
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
}
