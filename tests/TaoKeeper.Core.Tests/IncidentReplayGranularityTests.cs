using System;
using System.Collections.Generic;
using TaoKeeper.Core.Modules.IncidentReplay;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>A15-4 按天/周粒度查看时间线（R-P15-4）。</summary>
public sealed class IncidentReplayGranularityTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 按天分组_同日事件归一组()
    {
        var events = new List<IncidentEvent>
        {
            new("install", Anchor.AddHours(-2), "安装 A"),
            new("crash", Anchor.AddHours(-1), "崩溃 B"),
            new("install", Anchor.AddDays(-2), "安装 C"),
        };
        var days = IncidentReplayEngine.GroupByGranularity(events, "day");
        Assert.Equal(2, days.Count);
        Assert.Equal(2, days[0].Events.Count);
    }

    [Fact]
    public void 按周分组_跨七天归一组()
    {
        var events = new List<IncidentEvent>
        {
            new("install", Anchor.AddHours(-2), "安装 A"),
            new("settings", Anchor.AddDays(-3), "改设置 B"),
        };
        var weeks = IncidentReplayEngine.GroupByGranularity(events, "week");
        Assert.Single(weeks);
        Assert.Equal(2, weeks[0].Events.Count);
    }

    [Fact]
    public void 粒度非法时回退按天()
    {
        var events = new List<IncidentEvent> { new("install", Anchor, "X") };
        var days = IncidentReplayEngine.GroupByGranularity(events, "day");
        var fallback = IncidentReplayEngine.GroupByGranularity(events, "nope");
        Assert.Equal(days.Count, fallback.Count);
    }
}
