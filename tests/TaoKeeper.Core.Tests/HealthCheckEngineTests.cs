using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>体检规则引擎：评分与发现清单，缺失项不产发现、不误扣分。</summary>
public class HealthCheckEngineTests
{
    [Fact]
    public void 全部未测时满分零发现()
    {
        var result = new HealthCheckEngine().Run(HealthContext.Empty);
        Assert.Equal(100, result.Score);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void 垃圾与重复文件超阈值各产一条发现并扣分()
    {
        var result = new HealthCheckEngine().Run(new HealthContext(
            JunkReclaimableBytes: 6L * 1024 * 1024 * 1024,   // 6GB → 警告扣15
            DuplicateReclaimableBytes: 3L * 1024 * 1024 * 1024, // 3GB → 建议扣5
            StartupItemCount: null,
            SystemDiskFreePercent: null));
        Assert.Equal(2, result.Findings.Count);
        Assert.Equal(100 - 15 - 5, result.Score);
        // 严重度高的排前面
        Assert.Equal(HealthSeverity.Warning, result.Findings[0].Severity);
        Assert.All(result.Findings, f => Assert.StartsWith("jump-", f.SuggestedActionId));
    }

    [Fact]
    public void 系统盘告急为最高严重度()
    {
        var result = new HealthCheckEngine().Run(new HealthContext(
            JunkReclaimableBytes: 0, DuplicateReclaimableBytes: 0,
            StartupItemCount: 0, SystemDiskFreePercent: 7.5));
        var finding = Assert.Single(result.Findings);
        Assert.Equal(HealthSeverity.Critical, finding.Severity);
        Assert.Equal(70, result.Score); // 100 − 30
        Assert.Equal("jump-space-analysis", finding.SuggestedActionId);
    }

    [Fact]
    public void 启动项阈值分档()
    {
        var engine = new HealthCheckEngine(new[] { new StartupItemRule() });
        Assert.Null(engine.Run(new HealthContext(null, null, 5, null)).Findings.FirstOrDefault());
        Assert.Equal(HealthSeverity.Advice,
            engine.Run(new HealthContext(null, null, 16, null)).Findings.Single().Severity);
        Assert.Equal(HealthSeverity.Warning,
            engine.Run(new HealthContext(null, null, 30, null)).Findings.Single().Severity);
    }

    [Fact]
    public void 扣分封底为零不会负分()
    {
        var findings = new List<IHealthRule> { new SystemDiskSpaceRule(), new JunkAccumulationRule() };
        var result = new HealthCheckEngine(findings).Run(new HealthContext(
            JunkReclaimableBytes: 10L * 1024 * 1024 * 1024, DuplicateReclaimableBytes: 0,
            StartupItemCount: null, SystemDiskFreePercent: 5.0));
        Assert.Equal(55, result.Score); // 100 − 30 − 15
        Assert.True(result.Score >= 0);
    }

    [Fact]
    public void 存储感知与虚拟内存提示仅在给出条件时出现()
    {
        var r1 = new HealthCheckEngine().Run(new HealthContext(null, null, null, null));
        Assert.DoesNotContain(r1.Findings, f => f.RuleId is "storage-sense" or "virtual-memory-hint");

        var r2 = new HealthCheckEngine().Run(new HealthContext(
            null, null, null, null, VirtualMemoryHint: "页面文件偏小，建议按推荐方案调整", StorageSenseEnabled: false));
        Assert.Equal(2, r2.Findings.Count);
        Assert.All(r2.Findings, f => Assert.Equal(HealthSeverity.Info, f.Severity));
        Assert.Equal(100, r2.Score); // Info 不扣分
    }
}
