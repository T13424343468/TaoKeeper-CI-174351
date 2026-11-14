using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>
/// 清理页分层语义与白盒展示测试（W6）：
/// 规则行分层口径可见（R-P3-2 白盒化）、确定垃圾默认真删/用户文件默认回收站的清单传递、
/// 「直接删除」勾选只影响用户文件层、R5 资源预算一句话常显。
/// </summary>
public class CleanupTierPageTests
{
    private static JunkScanResult Scan() => new(
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        new List<JunkCandidate>
        {
            // 规则行：
            new(new JunkRule("system-temp", "系统临时文件", "临时", new[]{"%TEMP%"}, null, null, JunkRisk.Low),
                @"C:\t\a.tmp", 100, DateTimeOffset.UtcNow, "临时"),
            new(new JunkRule("installer-residue", "安装包残留", "残留", new[]{"%USERPROFILE%\\Downloads"}, null, 30, JunkRisk.High),
                @"C:\d\setup.exe", 200, DateTimeOffset.UtcNow, "残留"),
        },
        300);

    [Fact]
    public void Rows_expose_visible_tier_evidence()
    {
        var vm = new DiskCleanupPageViewModel().Build(Scan());

        var temp = vm.Rows.Single(r => r.RuleId == "system-temp");
        var residue = vm.Rows.Single(r => r.RuleId == "installer-residue");

        Assert.Equal(CleanupTier.Garbage, temp.Tier);
        Assert.Equal(CleanupTier.UsersFile, residue.Tier);
        Assert.Contains("规则 system-temp", temp.TierText);   // 白盒：规则 Id 同屏
        Assert.Contains("真删", temp.TierText);
        Assert.Contains("回收站", residue.TierText);
    }

    [Fact]
    public void Confirmed_actions_carry_tier_per_row()
    {
        var vm = new DiskCleanupPageViewModel().Build(Scan());
        vm.ToggleRule("installer-residue", on: true); // 高风险默认不勾，显式勾上再验证分层传递

        var actions = vm.BuildConfirmedActions();

        Assert.Contains(actions, a => a.Tier == CleanupTier.Garbage);
        Assert.Contains(actions, a => a.Tier == CleanupTier.UsersFile);
        Assert.All(actions, a => Assert.False(a.DirectDelete)); // 默认无直接删除
    }

    [Fact]
    public void Direct_delete_flag_only_affects_users_file_layer()
    {
        var vm = new DiskCleanupPageViewModel().Build(Scan());
        vm.ToggleRule("installer-residue", on: true);
        vm.DirectDeleteUserFiles = true;

        var actions = vm.BuildConfirmedActions();

        Assert.All(actions.Where(a => a.Tier == CleanupTier.Garbage), a => Assert.False(a.DirectDelete));
        Assert.All(actions.Where(a => a.Tier == CleanupTier.UsersFile), a => Assert.True(a.DirectDelete));
    }

    [Fact]
    public void Budget_statement_is_always_available()
    {
        var vm = new DiskCleanupPageViewModel();

        Assert.Equal(CleanupTierPolicy.BudgetStatement, vm.BudgetText);
        Assert.Contains("50 MB", vm.BudgetText);
    }
}
