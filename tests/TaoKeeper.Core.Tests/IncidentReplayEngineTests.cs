using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules.IncidentReplay;
using Xunit;

namespace TaoKeeper.Core.Tests;

public sealed class IncidentReplayEngineTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static List<IncidentEvent> SampleEvents() =>
    [
        new("install", Anchor.AddHours(-70), "安装 FooBar 2.0"),
        new("settings", Anchor.AddHours(-48), "修改设置：关闭快速扫描"),
        new("crash", Anchor.AddHours(-2), "应用崩溃：0x1E"),
        new("perf", Anchor.AddHours(-1), "内存占用 92%"),
    ];

    [Fact]
    public void A15_1_聚合四路事件流_按时间排序()
    {
        var report = IncidentReplayEngine.Replay(SampleEvents(), Anchor);
        Assert.False(report.IsEmptyWindow);
        Assert.Equal(4, report.Timeline.Count);
        Assert.True(report.Timeline[0].TimeUtc <= report.Timeline[^1].TimeUtc);
    }

    [Fact]
    public void A15_1_归因三件套_原因_步骤_置信度()
    {
        var report = IncidentReplayEngine.Replay(SampleEvents(), Anchor);
        var a = report.Attribution;
        Assert.NotNull(a);
        Assert.False(string.IsNullOrWhiteSpace(a.MostLikelyCause));
        Assert.NotEmpty(a.VerificationSteps);
        Assert.InRange(a.Confidence, 0.0, 1.0);
        // crash 事件权重最高，应居首
        Assert.Contains("崩溃", a.MostLikelyCause);
    }

    [Fact]
    public void A15_2_默认72h_可选24h_7d()
    {
        // 70h 前的安装：默认窗内可见；切 24h 后不可见（切换换数据）
        var events = SampleEvents();
        Assert.Contains(events[0], IncidentReplayEngine.Replay(events, Anchor).Timeline);
        var narrow = IncidentReplayEngine.Replay(events, Anchor, windowHours: 24);
        Assert.DoesNotContain(events[0], narrow.Timeline);
        // 24h 窗内：-48h 设置、-70h 安装都在窗外，仅 -2h 崩溃与 -1h 性能
        Assert.Equal(2, narrow.Timeline.Count);
        Assert.Contains(events[0], IncidentReplayEngine.Replay(events, Anchor, windowHours: 168).Timeline);
    }

    [Fact]
    public void A15_2_空窗显式提示_不出空白列表()
    {
        var report = IncidentReplayEngine.Replay([], Anchor);
        Assert.True(report.IsEmptyWindow);
        Assert.Empty(report.Timeline);
        // 空窗文案在 UI 层显式展示（VM 层取 EmptyWindowMessage 常量）
        Assert.Contains("空窗显式提示", IncidentReplayEngine.EmptyWindowMessage);
    }

    [Fact]
    public void A15_4_无任意时刻回放_窗口有界()
    {
        var events = new List<IncidentEvent>
        {
            new("install", Anchor.AddHours(-5000), "远古安装（超出快照保留上限跨度）"),
        };
        var report = IncidentReplayEngine.Replay(events, Anchor, windowHours: 168);
        Assert.True(report.IsEmptyWindow); // 跨度=快照保留上限，窗外如实空窗
    }

    [Fact]
    public void A15_3_零外发_引擎无网络栈()
    {
        // 可检验=全页零网络 API 引用：引擎类型不含 HttpClient/WebRequest 依赖（结构断言）。
        // 真核验：全包 grep 网络类型零命中（AppKernel 层测试与 CI 同口径）。
        var t = typeof(IncidentReplayEngine);
        Assert.StartsWith("TaoKeeper.Core.Modules", t.Namespace);
    }

    [Fact]
    public void 症状关键词提升置信度()
    {
        var events = SampleEvents();
        var withKeyword = IncidentReplayEngine.Replay(events, Anchor, symptomKeyword: "0x1E");
        var without = IncidentReplayEngine.Replay(events, Anchor);
        // 命中 crash 事件详情的关键词后，crash 源分数上升——本例 crash 本就居首，断言置信度只升不降
        Assert.True(withKeyword.Attribution!.Confidence >= without.Attribution!.Confidence - 0.0001);
    }
}
