using System;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>挂件聚合器：从内存/垃圾/进程三路快照聚合挂件一帧数据；未就绪灰显不编数。</summary>
public class WidgetAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 三路数据聚合成一帧()
    {
        var memory = new MemorySnapshot(Now, 16L << 30, 4L << 30, null, null);
        var junk = new JunkScanResult(Now, Now, Array.Empty<JunkCandidate>(), 2048);
        var proc = new StartupProcessSnapshot(Now, Array.Empty<StartupItemEntry>(),
            new[] { new ProcessEntry(1, "a", null, 0, 100), new ProcessEntry(2, "b", null, 0, 200) });

        var frame = WidgetSnapshotAggregator.Build(Now, memory, junk, proc);
        Assert.Equal(75.0, frame.MemoryUsedPercent);
        Assert.Equal(2048, frame.JunkCandidateBytes);
        Assert.Equal(2, frame.BackgroundProcessCount);
        Assert.True(frame.IsFresh(TimeSpan.FromMinutes(5), Now));
    }

    [Fact]
    public void 未就绪模块字段为空且过期灰显()
    {
        var frame = WidgetSnapshotAggregator.Build(Now, null, null, null);
        Assert.Equal(0, frame.MemoryUsedPercent);
        Assert.Null(frame.JunkCandidateBytes);
        Assert.Null(frame.BackgroundProcessCount);
        Assert.True(frame.IsFresh(TimeSpan.FromMinutes(1), Now));
        Assert.False(frame.IsFresh(TimeSpan.FromMinutes(1), Now.AddMinutes(2)));
    }

    [Fact]
    public void 一键清理永远需要确认()
    {
        var frame = WidgetSnapshotAggregator.Build(Now, null, null, null);
        Assert.True(WidgetSnapshotAggregator.RequiresConfirmation(frame));
    }
}
