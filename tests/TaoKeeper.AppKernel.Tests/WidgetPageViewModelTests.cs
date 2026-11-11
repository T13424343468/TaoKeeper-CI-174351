using System;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>桌面挂件页视图模型测试：开关意图转述、一帧数据转述与灰显、一键清理确认闸。</summary>
public class WidgetPageViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 2, 40, 0, TimeSpan.Zero);

    private static WidgetFrame Frame(
        double mem = 62,
        long? junkBytes = 1500L * 1024 * 1024,
        int? junkCount = 320,
        int? background = 87,
        DateTimeOffset? captured = null) =>
        new(captured ?? Now, mem, junkBytes, junkCount, background);

    [Fact]
    public void 关闭挂件_意图为移除自启并退出_零后台残留()
    {
        var vm = new WidgetPageViewModel();
        var action = vm.SetWidgetEnabled(false, Now);
        Assert.Equal(WidgetBootIntent.RemoveAutostartAndExit, action.Intent);
        Assert.False(vm.Settings.Enabled);
        Assert.Contains("不留后台进程", action.UserHint);
    }

    [Fact]
    public void 开启挂件_意图为登记自启_下次登录生效()
    {
        var vm = new WidgetPageViewModel();
        var action = vm.SetWidgetEnabled(true, Now);
        Assert.Equal(WidgetBootIntent.RegisterAutostart, action.Intent);
        Assert.True(vm.Settings.Enabled);
    }

    [Fact]
    public void 一帧数据转述_未就绪灰显不编数()
    {
        var vm = new WidgetPageViewModel();
        Assert.True(vm.DataStale);                 // 无数据=灰显
        Assert.Equal(string.Empty, vm.MemoryText);
        Assert.Contains("未就绪", vm.JunkText);

        vm.ApplyFrame(Frame(), Now);
        Assert.False(vm.DataStale);
        Assert.Equal("62%", vm.MemoryText);
        Assert.Contains("1.5 GB", vm.JunkText);
        Assert.Contains("320 项", vm.JunkText);
        Assert.Contains("后台进程 87 个", vm.BackgroundText);

        vm.ApplyFrame(Frame(junkBytes: null, junkCount: null), Now);
        Assert.Contains("未就绪", vm.JunkText);    // 单项缺失如实说明
        Assert.Equal("62%", vm.MemoryText);        // 其余项照常
    }

    [Fact]
    public void 数据过期灰显_不展示旧数()
    {
        var vm = new WidgetPageViewModel();
        vm.ApplyFrame(Frame(), Now);
        Assert.False(vm.DataStale);

        vm.RefreshFreshness(Now.AddMinutes(2));    // 超过 90 秒窗口
        Assert.True(vm.DataStale);
    }

    [Fact]
    public void 一键清理必须过确认_待确认态可解除()
    {
        var vm = new WidgetPageViewModel();
        vm.ApplyFrame(Frame(), Now);
        Assert.False(vm.CleanRequestPending);

        var hint = vm.PrepareCleanRequest();
        Assert.True(vm.CleanRequestPending);
        Assert.Contains("回收站", hint);
        Assert.Contains("确认", hint);

        vm.CompleteCleanRequest();
        Assert.False(vm.CleanRequestPending);
    }

    [Fact]
    public void 自启条目转述_归属本产品_可关闭()
    {
        var vm = new WidgetPageViewModel();
        var entry = vm.StartupEntryForDisplay(actuallyEnabled: true);
        Assert.Equal(WidgetSettings.AutostartItemId, entry.Id);
        Assert.Equal(CleanWidgetPolicy.OwnProductId, entry.OwningProductId);
        Assert.True(entry.Enabled);
    }

    [Fact]
    public void 启动项页过滤_管家自身不进建议禁用()
    {
        var vm = new WidgetPageViewModel();
        var own = vm.StartupEntryForDisplay(actuallyEnabled: true);
        var rogue = new StartupItemEntry("rogue", "某推广助手", StartupItemSource.RegistryRun,
            "未知", true, StartupRiskLevel.High, "常见流氓推广位");
        var (items, recommended) = vm.FilterStartupPage(new[] { own, rogue });
        Assert.Equal(2, items.Count);                          // 全部原样展示（透明）
        Assert.Single(recommended);
        Assert.Equal("rogue", recommended[0].Id);              // 自有组件绝不进建议
    }
}
