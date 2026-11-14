using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// G8 老机型优化测试：低配档位判据边界、冷启动延迟规划、启动项三档（改前快照钩子抛错即中止）、
/// 碎片整理规划（机械=提供/SSD=隐藏/未知=降级说明；执行前后读数实测一致）、老机器建议卡（只建议不执行）。
/// </summary>
public class LegacyOptimizationTests
{
    private const long GiB = 1024L * 1024 * 1024;

    // ==================== 档位判定边界 ====================

    [Fact]
    public void Detector_HighSpec_IsStandardTier()
    {
        var profile = LegacyTierDetector.Evaluate(new MachineProbe(8, 16 * GiB, IsMechanicalDisk: false));
        Assert.False(profile.LowSpecTier);
        Assert.True(profile.DecorationsEnabled);
        Assert.True(profile.AnimationsEnabled);
        Assert.Equal(LegacyTierMode.Auto, profile.SourceMode);
    }

    [Theory]
    [InlineData(2, 16L * GiB, true)]  // 核数 ≤2：低配
    [InlineData(4, 4L * GiB, true)]   // 内存 ≤4GB：硬低配
    [InlineData(4, 8L * GiB, true)]   // 4 核 8GB：低配边界
    [InlineData(4, 8L * GiB + 1, false)] // 4 核超 8GB：标准档
    [InlineData(6, 8L * GiB, false)]  // 6 核 8GB：标准档（核数判据只看 ≤2）
    public void Detector_Boundaries_MatchCriteria(int cores, long memory, bool expectLow)
    {
        var profile = LegacyTierDetector.Evaluate(new MachineProbe(cores, memory, IsMechanicalDisk: null));
        Assert.Equal(expectLow, profile.LowSpecTier);
        Assert.Equal(!expectLow, profile.DecorationsEnabled); // V-A9：装饰层随档位关/开，可断言
        Assert.Equal(!expectLow, profile.AnimationsEnabled);
    }

    [Fact]
    public void Detector_UnknownMemory_DoesNotDowngradeByMemoryRule()
    {
        // 内存读不到（≤0）不参与判据：8 核 0 内存按核数标准档，不误降级
        var profile = LegacyTierDetector.Evaluate(new MachineProbe(8, 0, IsMechanicalDisk: null));
        Assert.False(profile.LowSpecTier);
    }

    [Fact]
    public void Detector_MechanicalDiskAlone_DoesNotDowngrade()
    {
        // 机械盘只出建议卡，不降档（口径同需求文档 G8）
        var profile = LegacyTierDetector.Evaluate(new MachineProbe(16, 32 * GiB, IsMechanicalDisk: true));
        Assert.False(profile.LowSpecTier);
    }

    [Fact]
    public void Detector_ManualMode_OverridesAuto()
    {
        var standard = LegacyTierDetector.Evaluate(new MachineProbe(2, 2 * GiB, null), LegacyTierMode.Standard);
        var saver = LegacyTierDetector.Evaluate(new MachineProbe(16, 32 * GiB, null), LegacyTierMode.Saver);
        Assert.False(standard.LowSpecTier);
        Assert.True(saver.LowSpecTier);
    }

    [Fact]
    public void Detector_ThreadPoolScalesWithCores()
    {
        var low = LegacyTierDetector.Evaluate(new MachineProbe(2, 2 * GiB, null));
        Assert.True(low.ThreadPoolTargetThreadCount >= 1);
        Assert.True(low.ThreadPoolTargetThreadCount <= 2); // 低配机线程池收缩
    }

    // ==================== 冷启动延迟加载规划 ====================

    [Fact]
    public void ColdStartPlanner_DefersOnlyNonCriticalWork()
    {
        var plan = ColdStartLazyPlanner.Plan(
            new[] { "startup-scan", "junk-scan", "boot-time", "installed-scan", "critical-render" },
            new HashSet<string> { "critical-render" });
        Assert.DoesNotContain("critical-render", plan); // 关键集内的模块绝不延迟
        Assert.Contains("junk-scan", plan);
        Assert.Equal(4, plan.Count);
    }

    // ==================== 启动项三档：快照钩子中止 ====================

    private sealed class RecordingAdapter : IStartupModeAdapter
    {
        public List<(string ItemId, StartupLaunchMode Mode)> Calls { get; } = new();
        public bool Fail { get; set; }

        public Task<StartupModeResult> SetModeAsync(string itemId, StartupLaunchMode mode, CancellationToken cancellation = default)
        {
            Calls.Add((itemId, mode));
            return Task.FromResult(new StartupModeResult(itemId, mode, !Fail, Fail ? "模拟失败" : null));
        }
    }

    [Fact]
    public async Task StartupModeCoordinator_SnapshotsBeforeChange()
    {
        var adapter = new RecordingAdapter();
        var order = new List<string>();
        var coordinator = new StartupModeCoordinator(adapter)
        {
            // 显式时序断言：快照未完成前绝不碰系统
            SnapshotBeforeChange = (id, mode) =>
            {
                order.Add("snapshot");
                Assert.Empty(adapter.Calls);
                return Task.CompletedTask;
            },
        };
        var result = await coordinator.ApplyAsync("item-1", StartupLaunchMode.Disabled);
        Assert.True(result.Success);
        Assert.Single(adapter.Calls);
        Assert.Equal("snapshot", Assert.Single(order));
    }

    [Fact]
    public async Task StartupModeCoordinator_SnapshotFailure_AbortsChange()
    {
        var adapter = new RecordingAdapter();
        var coordinator = new StartupModeCoordinator(adapter)
        {
            SnapshotBeforeChange = (_, _) => throw new InvalidOperationException("快照落盘失败"),
        };
        var result = await coordinator.ApplyAsync("item-1", StartupLaunchMode.Delayed);
        Assert.False(result.Success); // 快照抛错即中止：零例外、零改动
        Assert.Empty(adapter.Calls);
        Assert.Contains("已中止", result.DisplayText);
        Assert.Contains("未做任何改动", result.DisplayText);
    }

    // ==================== 碎片整理规划 ====================

    [Fact]
    public void DefragPlanner_OfferMatrix_MatchesDiskType()
    {
        Assert.True(DefragPlanner.ShouldOffer(new DriveAnalysis("C:", IsMechanicalDisk: true, FragmentationPercent: 12)));
        Assert.False(DefragPlanner.ShouldOffer(new DriveAnalysis("C:", IsMechanicalDisk: false, FragmentationPercent: 0)));
        Assert.Null(DefragPlanner.ShouldOffer(new DriveAnalysis("C:", IsMechanicalDisk: null, FragmentationPercent: null)));
    }

    [Fact]
    public async Task DefragPlanner_Run_ReadsBeforeAndAfter()
    {
        // 官方分析读数序列：整理前 18% → 整理后复读 3%（前后同屏可断言 V-A8）
        var reads = new Queue<double?>(new double?[] { 18, 3 });
        var runner = new FakeDefragRunner
        {
            GetPercent = _ => reads.Dequeue(),
            Defrag = _ => new DefragOutcome(true, null, null),
        };
        var outcome = await DefragPlanner.RunAsync(runner, "C:");
        Assert.True(outcome.Success);
        Assert.Equal(18, outcome.BeforePercent);
        Assert.Equal(3, outcome.AfterPercent);
        Assert.Contains("18", outcome.DisplayText);
        Assert.Contains("3", outcome.DisplayText);
    }

    [Fact]
    public async Task DefragPlanner_Failure_ReportsHonestlyWithoutAfterReading()
    {
        var runner = new FakeDefragRunner
        {
            GetPercent = _ => 22,
            Defrag = _ => new DefragOutcome(false, null, null, "整理进程返回非零（需要管理员权限）"),
        };
        var outcome = await DefragPlanner.RunAsync(runner, "C:");
        Assert.False(outcome.Success);
        Assert.Null(outcome.AfterPercent); // 未完成不补读数
        Assert.Contains("管理员", outcome.DisplayText);
    }

    private sealed class FakeDefragRunner : IDefragRunner
    {
        public Func<string, double?> GetPercent { get; init; } = _ => null;
        public Func<string, DefragOutcome> Defrag { get; init; } = _ => new DefragOutcome(false, null, null, "未接线");

        public Task<DefragOutcome> DefragAsync(string volume, IProgress<string>? progress = null, CancellationToken cancellation = default)
            => Task.FromResult(Defrag(volume));

        public Task<double?> GetFragmentationPercentAsync(string volume, CancellationToken cancellation = default)
            => Task.FromResult(GetPercent(volume));
    }

    // ==================== 老机器建议卡 ====================

    [Fact]
    public void AdviceBuilder_MechanicalDisk_GetsDefragAndPowerCards()
    {
        var cards = OldMachineAdviceBuilder.Build(new MachineProbe(16, 32 * GiB, IsMechanicalDisk: true));
        Assert.Contains(cards, static c => c.Id == "defrag-hdd");
        Assert.Contains(cards, static c => c.Id == "power-plan");
        // 只建议不静默改：卡片不带执行动作（契约只有文案字段）
        Assert.All(cards, static c => Assert.False(string.IsNullOrWhiteSpace(c.Evidence)));
    }

    [Fact]
    public void AdviceBuilder_SsdHighSpec_NoCards()
    {
        var cards = OldMachineAdviceBuilder.Build(new MachineProbe(16, 32 * GiB, IsMechanicalDisk: false));
        Assert.Empty(cards); // SSD + 高配：零建议（不装忙）
    }

    [Fact]
    public void AdviceBuilder_LowSpecTier_GetsVisualFxCard()
    {
        var cards = OldMachineAdviceBuilder.Build(new MachineProbe(2, 2 * GiB, IsMechanicalDisk: false));
        Assert.Contains(cards, static c => c.Id == "visual-fx");
    }
}
