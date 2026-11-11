using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>一键加速编排测试：只建议不代做、确认后才执行、内存压力联动。</summary>
public class SpeedupCoordinatorTests
{
    private static StartupItemEntry Item(
        string id, StartupRiskLevel risk, bool enabled, string? hint = null, string? owner = null) =>
        new(id, "项-" + id, StartupItemSource.RegistryRun, "测试厂商", enabled, risk, hint,
            Command: "C:\\x\\" + id + ".exe", OwningProductId: owner);

    private static MemorySnapshot Memory(double usedPercent) =>
        new(DateTimeOffset.UtcNow,
            TotalPhysicalBytes: 16_000_000_000,
            AvailablePhysicalBytes: (long)(16_000_000_000 * (1 - usedPercent / 100.0)),
            PageFileTotalBytes: null, PageFileUsedBytes: null);

    [Fact]
    public async Task 计划分流_高危进禁用_中危进复核_其余不打扰()
    {
        var adapter = new InMemoryStartupAdapter(new List<StartupItemEntry>
        {
            Item("h1", StartupRiskLevel.High, enabled: true),      // → 禁用建议
            Item("h2", StartupRiskLevel.High, enabled: false),     // 已禁用：不打扰
            Item("m1", StartupRiskLevel.Medium, enabled: true),    // → 复核
            Item("l1", StartupRiskLevel.Low, enabled: true),       // 低风险：不打扰
        });
        var coordinator = new SpeedupCoordinator(adapter);

        var plan = await coordinator.PlanSpeedupAsync();

        var disable = plan.DisableSuggestions.Single();
        Assert.Equal("h1", disable.Item.Id);
        Assert.Contains("确认", disable.Reason); // 建议文案必须带确认语义
        var review = plan.ReviewSuggestions.Single();
        Assert.Equal("m1", review.Item.Id);
        Assert.False(plan.MemoryTrimSuggested);
        Assert.False(string.IsNullOrEmpty(plan.SummaryText));
    }

    [Fact]
    public async Task 计划阶段绝不执行任何动作()
    {
        var toggles = 0;
        var adapter = new InMemoryStartupAdapter(
            new List<StartupItemEntry> { Item("h1", StartupRiskLevel.High, enabled: true) },
            toggleHandler: (_, _, _) => { toggles++; return new StartupToggleResult("h1", false, true); });
        var coordinator = new SpeedupCoordinator(adapter);

        await coordinator.PlanSpeedupAsync();
        Assert.Equal(0, toggles); // 红线：计划只读，写动作只能走 ApplyDisableAsync
    }

    [Fact]
    public async Task 内存压力达到阈值才建议释放()
    {
        var adapter = new InMemoryStartupAdapter();
        var coordinator = new SpeedupCoordinator(adapter);

        var calm = await coordinator.PlanSpeedupAsync(memory: Memory(60));
        Assert.False(calm.MemoryTrimSuggested);
        Assert.Null(calm.MemoryNote);

        var pressure = await coordinator.PlanSpeedupAsync(memory: Memory(92));
        Assert.True(pressure.MemoryTrimSuggested);
        Assert.NotNull(pressure.MemoryNote);
    }

    [Fact]
    public async Task 执行只限确认条目_未确认跳过()
    {
        var executed = new List<string>();
        var adapter = new InMemoryStartupAdapter(
            toggleHandler: (string id, StartupItemEntry? _, bool _) => { executed.Add(id); return new StartupToggleResult(id, false, true); });
        var coordinator = new SpeedupCoordinator(adapter);

        var results = await coordinator.ApplyDisableAsync(new List<(string, bool)>
        {
            ("a", true),
            ("b", false), // 未确认：必须跳过
        });

        Assert.Equal(new[] { "a" }, executed);
        Assert.Equal(2, results.Count);
        Assert.True(results[0].Success);
        Assert.False(results[1].Success);
        Assert.Contains("确认", results[1].Detail);
        Assert.Equal("已禁用，下次开机生效", results[0].DisplayText);
    }

    [Fact]
    public async Task 适配层失败原样回填不吞异常()
    {
        var adapter = new InMemoryStartupAdapter(
            toggleHandler: (string id, StartupItemEntry? _, bool _) => new StartupToggleResult(id, false, false, "注册表键被占用"));
        var coordinator = new SpeedupCoordinator(adapter);

        var results = await coordinator.ApplyDisableAsync(new List<(string, bool)> { ("x", true) });
        Assert.False(results[0].Success);
        Assert.Equal("注册表键被占用", results[0].Detail);
        Assert.Equal("注册表键被占用", results[0].DisplayText); // 失败回执展示适配层给的原因
    }

    [Fact]
    public async Task 本产品自带组件单独标注不与第三方混算()
    {
        var adapter = new InMemoryStartupAdapter(new List<StartupItemEntry>
        {
            Item("keeper-widget", StartupRiskLevel.High, enabled: true, owner: "TaoKeeper"),
        });
        var coordinator = new SpeedupCoordinator(adapter);

        var plan = await coordinator.PlanSpeedupAsync();
        var suggestion = Assert.Single(plan.DisableSuggestions);
        Assert.Equal("TaoKeeper", suggestion.Item.OwningProductId); // 界面侧单独呈现
    }
}
