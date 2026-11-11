using System;
using System.Linq;
using System.Threading.Tasks;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>
/// 性能加速页进程排行数据源测试（D 集成线）：快照未接线如实空置、接线后按工作集降序、
/// 超量截前 10、缺失发布者如实省略——数据契约直接消费 Core 既有 StartupProcessSnapshot.Processes。
/// </summary>
public class LineD_BoostProcessRankingTests
{
    private static ProcessEntry Entry(int pid, long workingSetBytes, double cpu = 12.5, string? publisher = null) =>
        new(pid, $"proc{pid}", publisher, cpu, workingSetBytes);

    private static StartupProcessSnapshot Snapshot(params ProcessEntry[] processes) =>
        new(DateTimeOffset.UtcNow, Array.Empty<StartupItemEntry>(), processes);

    [Fact]
    public void 快照未接线时进程排行如实空置()
    {
        var vm = new SpeedupPageViewModel();

        Assert.Empty(vm.ProcessRanking);
    }

    [Fact]
    public void 接线后按工作集降序排行()
    {
        var vm = new SpeedupPageViewModel
        {
            ProcessSnapshotProvider = () => Snapshot(
                Entry(1, 100L * 1024 * 1024),
                Entry(2, 400L * 1024 * 1024),
                Entry(3, 200L * 1024 * 1024)),
        };

        var ranking = vm.ProcessRanking;
        Assert.Equal(new[] { 2, 3, 1 }, ranking.Select(static p => p.ProcessId).ToArray());
        Assert.Contains("PID 2", SpeedupPageViewModel.Describe(ranking[0]));
    }

    [Fact]
    public Task 快照超量只取前十条()
    {
        var processes = Enumerable.Range(1, 15)
            .Select(static i => new ProcessEntry(i, $"proc{i}", null, 1.0, i * 1024L * 1024))
            .ToArray();
        var vm = new SpeedupPageViewModel { ProcessSnapshotProvider = () => Snapshot(processes) };

        Assert.Equal(10, vm.ProcessRanking.Count);
        Assert.Equal(15, vm.ProcessRanking[0].ProcessId);
        return Task.CompletedTask;
    }

    [Fact]
    public void 提供者返回空快照如实空()
    {
        var vm = new SpeedupPageViewModel { ProcessSnapshotProvider = () => Snapshot() };

        Assert.Empty(vm.ProcessRanking);
    }

    [Fact]
    public void 进程转述缺失发布者如实省略()
    {
        var withPublisher = SpeedupPageViewModel.Describe(Entry(7, 150L * 1024 * 1024, publisher: "某厂商"));
        var withoutPublisher = SpeedupPageViewModel.Describe(Entry(7, 150L * 1024 * 1024));

        Assert.Contains("某厂商", withPublisher);
        Assert.DoesNotContain("某厂商", withoutPublisher);
        Assert.Contains("150 MiB", withoutPublisher);
        Assert.Contains("CPU 12.5%", withoutPublisher);
        Assert.Contains("proc7", withoutPublisher);
    }
}
