using System;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>
/// G8 加速页 VM 层测试：老机型档位探测与建议卡、启动项三档（未接线如实提示/接线执行）、
/// 碎片整理（未接线如实提示不写盘；接线后走官方接口口径）。
/// </summary>
public class BoostLegacyOptimizationTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void MachineAdvice_WithoutProbe_IsEmpty()
    {
        var vm = new SpeedupPageViewModel();
        Assert.Empty(vm.MachineAdvice); // 未探测为空清单，不假建议
        Assert.Null(vm.LegacyTier);
    }

    [Fact]
    public void ApplyMachineProbe_LowSpec_GetsTierAndAdviceCard()
    {
        var vm = new SpeedupPageViewModel();
        var profile = vm.ApplyMachineProbe(new MachineProbe(2, 2 * GiB, IsMechanicalDisk: false));
        Assert.True(profile.LowSpecTier);
        Assert.Contains(vm.MachineAdvice, static c => c.Id == "visual-fx"); // V-A9：建议卡随档位可断言
        Assert.All(vm.MachineAdvice, static c => Assert.False(string.IsNullOrWhiteSpace(c.Evidence)));
    }

    [Fact]
    public void ApplyMachineProbe_HighSpecSsd_NoAdvice()
    {
        var vm = new SpeedupPageViewModel();
        vm.ApplyMachineProbe(new MachineProbe(16, 32 * GiB, IsMechanicalDisk: false));
        Assert.Empty(vm.MachineAdvice);
    }

    [Fact]
    public async Task ApplyStartupMode_Unwired_HonestMessageNoAction()
    {
        var vm = new SpeedupPageViewModel(); // 未接线适配器
        var result = await vm.ApplyStartupModeAsync("item-1", StartupLaunchMode.Disabled);
        Assert.False(result.Success);
        Assert.Contains("尚未接线", result.DisplayText);
        Assert.Contains("未做任何改动", result.DisplayText);
        Assert.Equal(vm.LastStartupModeResult, result);
    }

    [Fact]
    public async Task ApplyStartupMode_Wired_ExecutesViaAdapter()
    {
        var vm = new SpeedupPageViewModel
        {
            StartupModeAdapter = new RecordingStartupAdapter(),
        };
        var result = await vm.ApplyStartupModeAsync("item-1", StartupLaunchMode.Delayed);
        Assert.True(result.Success);
        Assert.Equal(StartupLaunchMode.Delayed, result.Mode);
    }

    [Fact]
    public async Task ApplyStartupMode_WithSnapshotHook_SnapshotsBeforeAdapterCall()
    {
        var adapter = new RecordingStartupAdapter();
        var vm = new SpeedupPageViewModel { StartupModeAdapter = adapter };
        var order = 0;
        vm.StartupModeSnapshot = (_, _) =>
        {
            Assert.Equal(0, order); // 快照先于适配器调用
            order = 1;
            return Task.CompletedTask;
        };
        var result = await vm.ApplyStartupModeAsync("item-1", StartupLaunchMode.Disabled);
        Assert.True(result.Success);
        Assert.Equal(1, order);
    }

    [Fact]
    public async Task RunDefrag_Unwired_HonestMessageNoDiskWrite()
    {
        var vm = new SpeedupPageViewModel();
        var outcome = await vm.RunDefragAsync("C:");
        Assert.False(outcome.Success);
        Assert.Null(outcome.BeforePercent);
        Assert.Null(outcome.AfterPercent);
        Assert.Contains("尚未接线", outcome.Detail);
        Assert.Contains("未做任何磁盘写入", outcome.Detail);
    }

    [Fact]
    public void DefragOffered_DelegatesToPlannerMatrix()
    {
        var vm = new SpeedupPageViewModel();
        Assert.True(vm.DefragOffered(new DriveAnalysis("C:", true, 12)));
        Assert.False(vm.DefragOffered(new DriveAnalysis("C:", false, 0)));
        Assert.Null(vm.DefragOffered(new DriveAnalysis("C:", null, null)));
    }

    private sealed class RecordingStartupAdapter : IStartupModeAdapter
    {
        public Task<StartupModeResult> SetModeAsync(string itemId, StartupLaunchMode mode, CancellationToken cancellation = default)
            => Task.FromResult(new StartupModeResult(itemId, mode, true));
    }
}
