using System;
using System.Linq;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>软件管家渠道与校验策略：官方渠道、哈希失配拒装、失败可见。</summary>
public class StorageSenseEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 默认计划关闭且永不触发()
    {
        var plan = StorageSensePlan.Default;
        Assert.False(plan.Enabled);
        Assert.Null(plan.NextRunUtc(T0));
        Assert.False(plan.IsDue(T0));
        Assert.Null(StorageSenseEngine.Evaluate(T0, plan, 10, 1024));
    }

    [Fact]
    public void 开启需至少一项范围且间隔至少一天()
    {
        Assert.Throws<ArgumentException>(() =>
            StorageSenseEngine.Enable(StorageSensePlan.Default, StorageCleanupScope.None, 7, true, T0));
        var plan = StorageSenseEngine.Enable(StorageSensePlan.Default,
            StorageCleanupScope.TempFiles | StorageCleanupScope.BrowserCache, 0, true, T0);
        Assert.Equal(1, plan.IntervalDays);
        Assert.True(plan.Enabled);
    }

    [Fact]
    public void 到点产出预览未到点不动作()
    {
        var plan = StorageSenseEngine.Enable(StorageSensePlan.Default, StorageCleanupScope.TempFiles, 7, true, T0);
        Assert.Null(StorageSenseEngine.Evaluate(T0, plan, 5, 100)); // 刚开启不算逾期
        var due = T0.AddDays(8);
        var preview = StorageSenseEngine.Evaluate(due, plan, 5, 512 * 1024 * 1024);
        Assert.NotNull(preview);
        Assert.Contains("回收站", preview!.ConfirmText);
        Assert.Contains("可还原", preview.ConfirmText);
    }

    [Fact]
    public void 关闭后到点也不触发()
    {
        var plan = StorageSenseEngine.Enable(StorageSensePlan.Default, StorageCleanupScope.TempFiles, 7, true, T0);
        var off = plan with { Enabled = false };
        Assert.Null(StorageSenseEngine.Evaluate(T0.AddDays(30), off, 5, 100));
    }
}

/// <summary>挂件数据通路与自洽策略：确认浮层、回收站语义、管家不自清自身。</summary>
public class CleanWidgetTests
{
    [Fact]
    public void 一键清理预览含确认文案与回收站语义()
    {
        var preview = WidgetCleanPreview.Create(junkCount: 12, junkBytes: 256L * 1024 * 1024, processCount: 3);
        Assert.Equal(2, preview.ItemSummaries.Count);
        Assert.Contains("回收站", preview.ConfirmText);
        Assert.Contains("可还原", preview.ConfirmText);
        Assert.Equal(256L * 1024 * 1024, preview.TotalReclaimableBytes);
        Assert.Contains("没有可清理", WidgetCleanPreview.Create(0, 0, 0).ConfirmText);
    }

    [Fact]
    public void 关闭挂件等于移除自启加退出零残留()
    {
        var off = new CleanWidgetPolicy().ApplyToggle(false);
        Assert.Equal(WidgetBootIntent.RemoveAutostartAndExit, off.Intent);
        Assert.Contains("不留后台进程", off.UserHint);
        var on = new CleanWidgetPolicy().ApplyToggle(true);
        Assert.Equal(WidgetBootIntent.RegisterAutostart, on.Intent);
        Assert.Contains("下次登录", on.UserHint);
    }

    [Fact]
    public void 启动项页展示自身但绝不列入建议禁用()
    {
        var self = new StartupItemEntry(WidgetSettings.AutostartItemId, "TaoKeeper 清理挂件",
            StartupItemSource.RegistryRun, "TaoKeeper", true, StartupRiskLevel.Low,
            Command: "C:\\Apps\\TaoKeeper\\widget.exe", OwningProductId: "TaoKeeper");
        var app = new StartupItemEntry("taokeeper-app", "TaoKeeper 主程序",
            StartupItemSource.RegistryRun, "TaoKeeper", true, StartupRiskLevel.Low,
            Command: "C:\\Apps\\TaoKeeper\\app.exe", OwningProductId: "TaoKeeper");
        var rogue = new StartupItemEntry("Rogue.Adware", "某推广助手",
            StartupItemSource.RegistryRun, null, true, StartupRiskLevel.High,
            Command: "C:\\Temp\\adware.exe");
        var ok = new StartupItemEntry("Good.App", "普通自启",
            StartupItemSource.RegistryRun, "Good", true, StartupRiskLevel.Medium,
            Command: "C:\\Apps\\Good\\good.exe");

        var (items, recommended) = new CleanWidgetPolicy().FilterForStartupPage(new[] { self, app, rogue, ok });
        Assert.Equal(4, items.Count); // 全部展示（包括管家自身，透明）
        Assert.Contains(recommended, r => r.Id == "Rogue.Adware");
        Assert.DoesNotContain(recommended, r => r.OwningProductId == "TaoKeeper");
        Assert.DoesNotContain(recommended, r => r.Id == "taokeeper-app");
        // 命令行带 TaoKeeper 的第三方项也不建议禁用（防误伤）
        var lookalike = ok with { Id = "Other.Tool", Command = "C:\\Tools\\TaoKeeperHelper.exe" };
        var (_, rec2) = new CleanWidgetPolicy().FilterForStartupPage(new[] { lookalike });
        Assert.DoesNotContain(rec2, r => r.Id == "Other.Tool");
    }
}
