using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules.ResidueDetect;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>A14-2 六路场景：L2 万链覆盖的沙箱等价路径（内存对象图 + 假节点适配器）。</summary>
public sealed class ResidueSixPathTests
{
    private sealed class FakeSource : IResidueNodeSource
    {
        public required IReadOnlyList<ResidueNode> Nodes { get; init; }
        public required IReadOnlyList<ResidueEdge> Edges { get; init; }
    }

    private static readonly string[] Root = [@"C:\Program Files\MyApp"];

    [Fact]
    public void 六路1_符号链接循环_不挂死_不误判()
    {
        // A→B→C→A 循环出边：BFS visited 守卫保证终止；循环节点不可达根 → 不进候选（无缺失目标，C1 保守进待确认）
        var src = new FakeSource
        {
            Nodes =
            [
                new(ResidueNodeType.File, @"C:\Link\A"),
                new(ResidueNodeType.File, @"C:\Link\B"),
                new(ResidueNodeType.File, @"C:\Link\C"),
            ],
            Edges =
            [
                new(@"C:\Link\A", @"C:\Link\B"),
                new(@"C:\Link\B", @"C:\Link\C"),
                new(@"C:\Link\C", @"C:\Link\A"),
            ],
        };
        var report = ResidueDetector.Analyze(src, Root); // 不抛不挂死即通过
        Assert.Empty(report.CleanupCandidates); // 循环节点无缺失目标 → 保守不判残留
        Assert.Equal(3, report.PendingConfirmation.Count);
    }

    [Fact]
    public void 六路2_硬链接_同目标引用_可达即不判残留()
    {
        // 两个路径节点边指向同一目标（硬链接语义）：目标可达则都不进候选
        var src = new FakeSource
        {
            Nodes =
            [
                new(ResidueNodeType.File, @"C:\Program Files\MyApp\core.dll"),
                new(ResidueNodeType.File, @"C:\Program Files\MyApp\alias.dll", Target: @"C:\Program Files\MyApp\core.dll"),
                new(ResidueNodeType.RegistryKey, @"HKCU\Software\MyApp\Run", Target: @"C:\Program Files\MyApp\alias.dll"),
            ],
            Edges = [new(@"HKCU\Software\MyApp\Run", @"C:\Program Files\MyApp\alias.dll")],
        };
        var report = ResidueDetector.Analyze(src, Root);
        Assert.Empty(report.CleanupCandidates);
    }

    [Fact]
    public void 六路3_权限边界_不可枚举节点_保守不判残留()
    {
        // Target=null 的不可达节点永远进待确认不进候选（宁保守 C1）
        var src = new FakeSource
        {
            Nodes =
            [
                new(ResidueNodeType.Service, @"svc\LockedSvc"), // 无目标信息（权限不可枚举）
                new(ResidueNodeType.File, @"C:\Denied\secret.dat"),
            ],
            Edges = [],
        };
        var report = ResidueDetector.Analyze(src, Root);
        Assert.Empty(report.CleanupCandidates);
        Assert.Equal(2, report.PendingConfirmation.Count);
    }

    [Fact]
    public void 六路4_计划任务_目标缺失_进候选()
    {
        var src = new FakeSource
        {
            Nodes =
            [
                new(ResidueNodeType.ScheduledTask, @"Task\GhostTask", Target: @"C:\Gone\runner.exe"),
            ],
            Edges = [],
        };
        var report = ResidueDetector.Analyze(src, Root);
        Assert.Single(report.CleanupCandidates);
    }

    [Fact]
    public void 六路5_服务指向活目标_不进候选不进待确认()
    {
        var src = new FakeSource
        {
            Nodes =
            [
                new(ResidueNodeType.Service, @"svc\LiveSvc", Target: @"C:\Program Files\MyApp\svc.exe"),
                new(ResidueNodeType.File, @"C:\Program Files\MyApp\svc.exe"),
            ],
            Edges = [],
        };
        var report = ResidueDetector.Analyze(src, Root);
        Assert.Empty(report.CleanupCandidates);
        Assert.Empty(report.PendingConfirmation);
    }

    [Fact]
    public void 六路6_多根_任一根可达即不判()
    {
        var src = new FakeSource
        {
            Nodes =
            [
                new(ResidueNodeType.File, @"D:\Alt\tool.exe"),
            ],
            Edges = [],
        };
        var report = ResidueDetector.Analyze(src, [@"C:\Program Files\MyApp", @"D:\Alt"]);
        Assert.Empty(report.CleanupCandidates);
        Assert.Empty(report.PendingConfirmation);
    }

    [Fact]
    public void A14_2_万链规模_一万节点秒级判定()
    {
        // L2 万链覆盖的沙箱等价：10000 节点链式图，判定耗时受限于 O(V+E)
        var nodes = new List<ResidueNode>();
        var edges = new List<ResidueEdge>();
        for (int i = 0; i < 10000; i++)
        {
            nodes.Add(new(ResidueNodeType.File, $@"C:\Chain\n{i}"));
            if (i > 0) edges.Add(new($@"C:\Chain\n{i}", $@"C:\Chain\n{i - 1}"));
        }
        var src = new FakeSource { Nodes = nodes, Edges = edges };
        var report = ResidueDetector.Analyze(src, [@"C:\Chain\n0"]);
        Assert.Empty(report.CleanupCandidates); // 全链可达
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ResidueDetector.Analyze(src, [@"C:\Chain\n0"]);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"万链判定耗时 {sw.ElapsedMilliseconds}ms 超阈值");
    }
}
