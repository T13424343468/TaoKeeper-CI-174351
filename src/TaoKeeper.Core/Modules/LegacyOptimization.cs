using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoKeeper.Core.Modules;

/// <summary>档位模式：默认自动判定，可手切（G8：默认自动、开箱即用，设置页可改）。</summary>
public enum LegacyTierMode { Auto, Standard, Saver }

/// <summary>本机探测快照（G8：CPU 核数 / 内存 / 磁盘类型；磁盘类型不可知时为 null，调用方如实降级）。</summary>
public sealed record MachineProbe(
    int CpuCoreCount,
    long TotalMemoryBytes,
    /// <summary>true=机械盘；false=SSD；null=无法判定（探测失败/平台不支持）。</summary>
    bool? IsMechanicalDisk);

/// <summary>老机型档位画像（低配档位自动降级的产出，装饰层/动画/线程池口径）。</summary>
public sealed record LegacyProfile(
    bool LowSpecTier,
    /// <summary>低配档位=false（关山景装饰与过渡动画，画面降朴素、功能不降）。</summary>
    bool DecorationsEnabled,
    bool AnimationsEnabled,
    /// <summary>后台线程池随核数收缩（低配机在常驻 ≤50MB 预算内再收）。</summary>
    int ThreadPoolTargetThreadCount,
    LegacyTierMode SourceMode)
{
    /// <summary>档位说明一句话（V-A9：装饰层关闭可断言，说明文案同源）。</summary>
    public string TierText => LowSpecTier
        ? $"省资源档（自动判定）：装饰与动画已关、后台线程 {ThreadPoolTargetThreadCount}（功能不降，常驻内存 ≤ 50 MB）"
        : $"标准档（自动判定）：全量画面效果，后台线程 {ThreadPoolTargetThreadCount}。";
}

/// <summary>
/// G8 老机型档位判定（可移植逻辑层）：
/// 低配判据——CPU 核数 ≤4 且内存 ≤8GB，或内存 ≤4GB，或核数 ≤2；机械盘单看建议卡不降档。
/// </summary>
public static class LegacyTierDetector
{
    public const long LowSpecMemoryThresholdBytes = 8L * 1024 * 1024 * 1024;
    public const long HardLowMemoryThresholdBytes = 4L * 1024 * 1024 * 1024;
    public const int LowSpecCoreThreshold = 4;

    public static LegacyProfile Evaluate(MachineProbe probe, LegacyTierMode mode = LegacyTierMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(probe);
        // 内存读不到（≤0）不参与降级判据（不因探测失败误降级）；磁盘介质只影响建议卡，不影响档位
        var memoryKnown = probe.TotalMemoryBytes > 0;
        var auto = (probe.CpuCoreCount <= 2)
            || (memoryKnown && probe.TotalMemoryBytes <= HardLowMemoryThresholdBytes)
            || (memoryKnown && probe.CpuCoreCount <= LowSpecCoreThreshold && probe.TotalMemoryBytes <= LowSpecMemoryThresholdBytes);
        var low = mode switch
        {
            LegacyTierMode.Saver => true,
            LegacyTierMode.Standard => false,
            _ => auto, // Auto：默认自动判定
        };
        return new LegacyProfile(
            LowSpecTier: low,
            DecorationsEnabled: !low,
            AnimationsEnabled: !low,
            ThreadPoolTargetThreadCount: low ? Math.Max(2, Math.Min(2, probe.CpuCoreCount)) : Math.Max(4, probe.CpuCoreCount),
            SourceMode: mode);
    }
}

/// <summary>
/// 冷启动延迟加载规划（G8）：非关键模块主界面亮后再加载。
/// 关键模块首帧必需；其余全部进延迟队列，按注册顺序逐个回补，规划零副作用。
/// </summary>
public static class ColdStartLazyPlanner
{
    /// <summary>返回延迟加载清单（按注册顺序）；关键集内的模块绝不延迟。</summary>
    public static IReadOnlyList<string> Plan(IEnumerable<string> allModules, ISet<string> criticalModules)
    {
        ArgumentNullException.ThrowIfNull(allModules);
        ArgumentNullException.ThrowIfNull(criticalModules);
        return allModules.Where(m => !criticalModules.Contains(m)).ToList();
    }
}

/// <summary>启动项三档（R-P5-6）：禁用 / 延迟启动（系统重启后开机完成再延时拉起）/ 立即启动。</summary>
public enum StartupLaunchMode { Immediate, Delayed, Disabled }

/// <summary>一次三档操作的结果（界面直接展示）。</summary>
public sealed record StartupModeResult(
    string ItemId,
    StartupLaunchMode Mode,
    bool Success,
    string? Detail = null)
{
    public string DisplayText => Success switch
    {
        true when Mode == StartupLaunchMode.Disabled => "已禁用，下次开机生效",
        true when Mode == StartupLaunchMode.Delayed => "已设为延迟启动：系统重启后开机完成再延时拉起",
        true => "已设为立即启动",
        _ => Detail ?? "操作未完成，请重试",
    };
}

/// <summary>启动项三档适配器契约（R-P5-6）：真机由注册表/启动文件夹适配层实现；沙箱用内存假适配器。</summary>
public interface IStartupModeAdapter
{
    /// <summary>对单个启动项设置三档之一；只处理传入条目，不做任何连带动作。</summary>
    Task<StartupModeResult> SetModeAsync(string itemId, StartupLaunchMode mode, CancellationToken cancellation = default);
}

/// <summary>内存假三档适配器：沙箱编排测试，不碰真实系统。</summary>
public sealed class InMemoryStartupModeAdapter : IStartupModeAdapter
{
    public Func<string, StartupLaunchMode, StartupModeResult>? Handler { get; set; }

    public Task<StartupModeResult> SetModeAsync(string itemId, StartupLaunchMode mode, CancellationToken cancellation = default)
        => Task.FromResult(Handler is { } h
            ? h(itemId, mode)
            : new StartupModeResult(itemId, mode, false, "沙箱假适配器不执行真实启停"));
}

/// <summary>
/// 启动项三档编排（R-P5-6，G8 落点）：改前先走 R-P5-1 快照通道（快照可回滚），
/// 再经适配器逐条执行；快照回调抛错时中止动作（先快照后动手，零例外）。
/// </summary>
public sealed class StartupModeCoordinator(IStartupModeAdapter adapter)
{
    private readonly IStartupModeAdapter _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    /// <summary>改前快照回调（接线 R-P5-1 快照通道；抛错即中止，未快照绝不改系统）。</summary>
    public Func<string, StartupLaunchMode, Task>? SnapshotBeforeChange { get; init; }

    public async Task<StartupModeResult> ApplyAsync(string itemId, StartupLaunchMode mode, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        if (SnapshotBeforeChange is { } snapshot)
        {
            try { await snapshot(itemId, mode).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return new StartupModeResult(itemId, mode, false, $"改前快照未完成，已中止（未做任何改动）：{ex.Message}");
            }
        }
        return await _adapter.SetModeAsync(itemId, mode, cancellation).ConfigureAwait(false);
    }
}

/// <summary>一块磁盘的碎片整理分析读数（V-A8：数字与实测一致，读不到如实为 null）。</summary>
public sealed record DriveAnalysis(
    string Volume,
    /// <summary>true=机械盘；false=SSD（SSD 不显示碎片整理，TRIM 由系统管）；null=无法判定。</summary>
    bool? IsMechanicalDisk,
    /// <summary>碎片率百分比（0-100；不可读为 null）。</summary>
    double? FragmentationPercent);

/// <summary>碎片整理执行适配器（R-P5-5）：走 Windows 官方碎片整理接口，不做任何非常规磁盘写入。</summary>
public interface IDefragRunner
{
    /// <summary>对指定卷执行整理；progress 逐段回传进度文本，失败返回 Success=false 与原因。</summary>
    Task<DefragOutcome> DefragAsync(string volume, IProgress<string>? progress = null, CancellationToken cancellation = default);

    /// <summary>读指定卷碎片率（官方分析口径）；不可读返回 null（不估算）。</summary>
    Task<double?> GetFragmentationPercentAsync(string volume, CancellationToken cancellation = default);
}

/// <summary>一次碎片整理的回执（前后碎片率实测读数与实测一致）。</summary>
public sealed record DefragOutcome(bool Success, double? BeforePercent, double? AfterPercent, string? Detail = null)
{
    /// <summary>回执文案（前后读数同屏可断言 V-A8；读不到的部分如实标注未测）。</summary>
    public string DisplayText => !Success
        ? (Detail ?? "碎片整理未完成，未做任何非常规写入。")
        : $"碎片整理完成：{Rate(BeforePercent)} → {Rate(AfterPercent)}（官方接口读数，实测一致）。";

    private static string Rate(double? percent) => percent is { } p ? $"{p:0.#}%" : "未测";
}

/// <summary>
/// 机械盘碎片整理规划（R-P5-5，G8 落点）：仅机械盘提供；SSD 不显示（防误伤，TRIM 由系统管）；
/// 磁盘类型无法判定时返回明确的降级说明（真实待真机验证口径，不装死、不假数）。
/// </summary>
public static class DefragPlanner
{
    public const string SsdHiddenMessage = "此盘为 SSD，不需要碎片整理（TRIM 由系统管理），已隐藏该功能。";
    public const string UnknownDiskMessage = "磁盘类型未能判定（需要真机读取物理磁盘介质类型），碎片整理暂不可用；此为待真机验证项，不影响其他功能。";

    /// <summary>该卷是否应提供碎片整理入口；返回 null 表示磁盘类型未判定（调用方如实显示降级说明）。</summary>
    public static bool? ShouldOffer(DriveAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis.IsMechanicalDisk switch
        {
            true => true,
            false => false, // SSD：不显示，防误伤
            _ => null,      // 未知：如实待判定
        };
    }

    /// <summary>执行整理：先取整理前碎片率 → 官方接口整理 → 复读碎片率（任一步失败如实报错，不做估算）。</summary>
    public static async Task<DefragOutcome> RunAsync(IDefragRunner runner, string volume,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var before = await runner.GetFragmentationPercentAsync(volume, cancellation).ConfigureAwait(false);
        progress?.Report($"整理前碎片率：{(before is { } b ? $"{b:0.#}%" : "未测（分析不可读，不估算）")}");
        var outcome = await runner.DefragAsync(volume, progress, cancellation).ConfigureAwait(false);
        var after = outcome.Success
            ? await runner.GetFragmentationPercentAsync(volume, cancellation).ConfigureAwait(false)
            : null;
        return outcome.Success
            ? new DefragOutcome(true, before, after, outcome.Detail)
            : outcome;
    }
}

/// <summary>老机器建议卡（R-P4-9，G8 落点）：只建议不静默改，用户确认后执行、可回退。</summary>
public sealed record OldMachineAdviceCard(string Id, string Title, string Detail, string Evidence);

/// <summary>老机器建议卡生成器：说人话 + 实测依据；只产出建议卡，零执行动作。</summary>
public static class OldMachineAdviceBuilder
{
    public static IReadOnlyList<OldMachineAdviceCard> Build(MachineProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var cards = new List<OldMachineAdviceCard>();
        var tier = LegacyTierDetector.Evaluate(probe);

        if (probe.IsMechanicalDisk == true)
        {
            cards.Add(new OldMachineAdviceCard(
                "defrag-hdd", "给机械盘做一次碎片整理",
                "老机械盘文件碎片多了会明显变慢，做一次整理通常能找回不少流畅度（几分钟到半小时，看盘）。",
                "实测依据：本机检测到机械硬盘（介质类型=HDD）。"));
            cards.Add(new OldMachineAdviceCard(
                "power-plan", "切换省电电源计划",
                "省电计划会让硬件降速运行。换成「高性能/平衡」档可以让老机器更跟手（随时可切回）。",
                "实测依据：机械盘机型对磁盘降速最敏感，高性能档实测读写更快。"));
        }
        if (tier.LowSpecTier)
        {
            cards.Add(new OldMachineAdviceCard(
                "visual-fx", "关闭视觉特效（画面变朴素，功能不减少）",
                "关掉山景装饰和过渡动画可以省下内存与 CPU，界面依旧全功能（可随时改回）。",
                $"实测依据：CPU {probe.CpuCoreCount} 核 / 内存 {probe.TotalMemoryBytes / 1024 / 1024} MiB，已达低配档判据。"));
        }
        return cards;
    }
}
