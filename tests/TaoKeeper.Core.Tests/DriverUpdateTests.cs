using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>驱动升级计划模块测试：策略闸门、备份前置、高危确认与哈希拦截。</summary>
public class DriverUpdateTests
{
    private static DriverUpdateEntry Make(
        string id = "PCI\\VEN_10DE",
        string? installed = "31.0.15.1234",
        string? available = "31.0.16.0001",
        DriverUpdateKind kind = DriverUpdateKind.WuaDriver,
        bool critical = true,
        bool? hash = null) =>
        new(id, "测试设备", installed, available, kind, critical, hash);

    [Fact]
    public void HasUpgrade_需要两侧版本都有()
    {
        var noInstalled = Make(installed: null);
        var noAvailable = Make(available: null);
        var same = Make(available: "31.0.15.1234");
        Assert.False(noInstalled.HasUpgrade);
        Assert.False(noAvailable.HasUpgrade);
        Assert.False(same.HasUpgrade);
        Assert.True(Make().HasUpgrade);
    }

    [Fact]
    public void 静默策略_高危或哈希未过一律不可静默()
    {
        var policy = new DriverUpdatePolicy(SilentUpgradeAllowed: true);
        var critical = Make(hash: true);
        var nonCriticalNoHash = Make(critical: false);
        var nonCriticalVerified = Make(critical: false, hash: true);

        Assert.False(policy.CanRunSilently(critical));          // 高危设备永不静默
        Assert.False(policy.CanRunSilently(nonCriticalNoHash)); // 哈希未过不可静默
        Assert.True(policy.CanRunSilently(nonCriticalVerified));
    }

    [Fact]
    public async Task 计划分类_静默确认拦截与备份计划()
    {
        var adapter = new InMemoryDriverUpdateAdapter(new List<DriverUpdateEntry>
        {
            Make("d1", critical: false, hash: true),        // → 静默
            Make("d2", critical: true, hash: true),         // → 确认（高危）
            Make("d3", critical: false, hash: false),       // → 拦截（哈希失配）
            Make("d4", installed: null, available: null),   // → 无升级，不进任何桶
        });
        var coordinator = new DriverUpdateCoordinator(adapter);

        var plan = await coordinator.PlanDriverUpdatesAsync(new DriverUpdatePolicy(SilentUpgradeAllowed: true));

        Assert.Single(plan.SilentAllowed);
        Assert.Single(plan.NeedConfirm);
        Assert.Single(plan.BlockedByPolicy);
        Assert.Equal(3, plan.BackupPlans.Count); // 只有需要升级的条目才出备份计划
        Assert.All(plan.BackupPlans, b => Assert.Contains("pnputil /export-driver", b.ExportCommand));
        Assert.Equal(DriverBackupPlan.StandardRollbackGuidance, plan.BackupPlans[0].RollbackGuidance);
    }

    [Fact]
    public async Task 升级闸门_备份失败必须拒绝且不碰升级()
    {
        var applied = false;
        var adapter = new InMemoryDriverUpdateAdapter(
            applyHandler: _ => { applied = true; return new DriverActionResult("d1", true, true); });
        var coordinator = new DriverUpdateCoordinator(adapter);

        var result = await coordinator.UpgradeAsync(
            Make(), new DriverUpdatePolicy(), backupDirectory: "/tmp/drv");

        Assert.False(result.Success);
        Assert.False(result.BackupCompleted);
        Assert.Equal(DriverActionResult.NoBackupDetail, result.Detail);
        Assert.False(applied); // 备份失败时适配层升级入口绝不触及
    }

    [Fact]
    public async Task 升级闸门_哈希失配即使备份完成也要拒绝()
    {
        var adapter = new InMemoryDriverUpdateAdapter(
            backupHandler: _ => true,
            applyHandler: _ => new DriverActionResult("d1", true, true));
        var coordinator = new DriverUpdateCoordinator(adapter);

        var result = await coordinator.UpgradeAsync(
            Make(hash: false), new DriverUpdatePolicy(), backupDirectory: "/tmp/drv");

        Assert.False(result.Success);
        Assert.True(result.BackupCompleted);
        Assert.Equal(DriverActionResult.HashMismatchDetail, result.Detail);
    }

    [Fact]
    public async Task 升级闸门_高危未确认不放行_确认后走适配层()
    {
        var calls = new List<string>();
        var adapter = new InMemoryDriverUpdateAdapter(
            backupHandler: _ => true,
            applyHandler: d => { calls.Add(d.DeviceId); return new DriverActionResult(d.DeviceId, true, true); });
        var coordinator = new DriverUpdateCoordinator(adapter);
        var policy = new DriverUpdatePolicy();

        var rejected = await coordinator.UpgradeAsync(
            Make(), policy, backupDirectory: "/tmp/drv", userConfirmed: false);
        Assert.False(rejected.Success);
        Assert.Contains("确认", rejected.Detail);
        Assert.Empty(calls);

        var accepted = await coordinator.UpgradeAsync(
            Make(), policy, backupDirectory: "/tmp/drv", userConfirmed: true);
        Assert.True(accepted.Success);
        Assert.Single(calls);
    }
}
