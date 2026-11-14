using System;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// W8 默认态契约验证：存储感知总开关默认关闭（用户逐项开启后才动）、
/// 弹窗规则引擎白名单优先（既有测试链路的默认态再确认）。
/// </summary>
public class StorageSenseDefaultOffTests
{
    [Fact]
    public void StorageSensePlan_Default_IsDisabled()
    {
        var plan = StorageSensePlan.Default;
        Assert.False(plan.Enabled); // 默认关：不开就绝不清
        Assert.Equal(StorageCleanupScope.None, plan.Scope); // 范围未勾选
        Assert.Null(plan.LastRunUtc); // 从未运行
    }

    [Fact]
    public void StorageSensePlan_Disabled_NeverDue()
    {
        var plan = StorageSensePlan.Default;
        Assert.False(plan.IsDue(DateTimeOffset.UtcNow)); // 关闭态永不到点（不静默清理）
        Assert.Null(plan.NextRunUtc(DateTimeOffset.UtcNow));
    }
}
