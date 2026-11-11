using System;
using System.Linq;
using System.Threading.Tasks;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Models;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>启动项页视图模型测试：只读加载、摘要统计、来源/风险/状态文案与空态如实（B 管家线 v2）。</summary>
public class LineB2_StartupPageViewModelTests
{
    private static StartupItemEntry Item(
        string id = "i1", string name = "某音乐播放器", StartupItemSource source = StartupItemSource.RegistryRun,
        string? publisher = "某厂商", bool enabled = true, StartupRiskLevel risk = StartupRiskLevel.Medium,
        string? command = null) =>
        new(id, name, source, publisher, enabled, risk, Command: command);

    private static IStartupControlAdapter Adapter(params StartupItemEntry[] items) =>
        new InMemoryStartupAdapter(items.ToList());

    [Fact]
    public Task 未扫描时显示未测态不假数据()
    {
        var vm = new StartupPageViewModel();

        Assert.False(vm.HasItems);
        Assert.Empty(vm.Items);
        Assert.Equal("尚未扫描启动项", vm.SummaryText);
        return Task.CompletedTask; // 无 await 的同步断言（消 CS1998，不改行为）
    }

    [Fact]
    public async Task 只读加载填清单与摘要统计()
    {
        var vm = new StartupPageViewModel();
        await vm.LoadAsync(Adapter(
            Item("a", enabled: true, risk: StartupRiskLevel.High),
            Item("b", enabled: true, risk: StartupRiskLevel.Low),
            Item("c", enabled: false, risk: StartupRiskLevel.Medium)));

        Assert.True(vm.HasItems);
        Assert.Equal(3, vm.Items.Count);
        Assert.Equal("共 3 项：启用中 2，已禁用 1，高风险 1 项", vm.SummaryText);
    }

    [Fact]
    public async Task 空清单摘要如实说明()
    {
        var vm = new StartupPageViewModel();
        await vm.LoadAsync(Adapter());

        Assert.False(vm.HasItems);
        Assert.Equal("扫描完成：没有发现开机启动项", vm.SummaryText);
    }

    [Fact]
    public void 来源风险状态文案与真实来源对应()
    {
        var vm = new StartupPageViewModel();
        var run = Item(source: StartupItemSource.RegistryRun, risk: StartupRiskLevel.High, enabled: true);
        var task = Item(source: StartupItemSource.ScheduledTask, risk: StartupRiskLevel.Medium, enabled: false);
        var folder = Item(source: StartupItemSource.StartupFolder, risk: StartupRiskLevel.Low, enabled: true);

        Assert.Equal("注册表 Run 键", StartupPageViewModel.SourceText(run.Source));
        Assert.Equal("计划任务", StartupPageViewModel.SourceText(task.Source));
        Assert.Equal("启动文件夹", StartupPageViewModel.SourceText(folder.Source));

        Assert.Equal("高风险", StartupPageViewModel.RiskText(run));
        Assert.Equal("未确认发布者", StartupPageViewModel.RiskText(task));
        Assert.Equal("低风险", StartupPageViewModel.RiskText(folder));

        Assert.Equal("启用中", StartupPageViewModel.StateText(run));
        Assert.Equal("已禁用", StartupPageViewModel.StateText(task));
    }

    [Fact]
    public void 明细行缺失字段如实省略不造假()
    {
        var full = Item(command: @"C:\app\music.exe");
        var bare = Item("b", publisher: null, command: null);

        Assert.Contains("发布者 某厂商", StartupPageViewModel.DetailText(full));
        Assert.Contains(@"C:\app\music.exe", StartupPageViewModel.DetailText(full));
        Assert.DoesNotContain("发布者", StartupPageViewModel.DetailText(bare));
    }

    [Fact]
    public async Task 空适配器参数立即失败不静默()
    {
        var vm = new StartupPageViewModel();
        await Assert.ThrowsAsync<ArgumentNullException>(() => vm.LoadAsync(null!));
    }
}
