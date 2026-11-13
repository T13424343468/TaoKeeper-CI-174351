using System;
using TaoKeeper.AppKernel.Navigation;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>体检页视图模型测试：评分带、未测兜底、严重度主题色、发现→页面路由跳转。</summary>
public class CheckupPageViewModelTests
{
    private static HealthContext Ctx(
        long? junk = null, int? startup = null, double? sysDisk = null) =>
        new(junk, null, startup, sysDisk);

    [Fact]
    public void 空上下文_未测态_不展示假100分()
    {
        var vm = new CheckupPageViewModel().RunCheckup(HealthContext.Empty);
        Assert.True(vm.IsUntested);
        Assert.Equal(0, vm.Score);
        Assert.Equal("尚未体检", vm.ScoreBandText);
        Assert.Empty(vm.Findings);
    }

    [Fact]
    public void 评分与发现_按严重度降序且分带正确()
    {
        // 系统盘 5%（Critical 30）+ 垃圾 6GB（Warning 15）+ 启动项 25（Warning 15）→ 100-60=40
        var vm = new CheckupPageViewModel().RunCheckup(Ctx(junk: 6L * 1024 * 1024 * 1024, startup: 25, sysDisk: 5));
        Assert.False(vm.IsUntested);
        Assert.Equal(40, vm.Score);
        Assert.Equal("建议全面清理", vm.ScoreBandText);
        Assert.Equal(3, vm.Findings.Count);
        Assert.Equal(HealthSeverity.Critical, vm.Findings[0].Severity);   // 最严重在前
        Assert.Equal(HealthSeverity.Warning, vm.Findings[1].Severity);
        Assert.Equal(new[] { "system-disk-space", "junk-accumulation", "startup-items" },
            vm.Findings.Select(f => f.RuleId));                           // 降序后规则序固定
    }

    [Fact]
    public void 发现项_路由映射齐全()
    {
        var vm = new CheckupPageViewModel().RunCheckup(Ctx(junk: 6L * 1024 * 1024 * 1024));
        var item = Assert.Single(vm.Findings);
        Assert.Equal(AppRoute.Disk, item.JumpRoute);                      // jump-disk-clean → 磁盘清理
    }

    [Fact]
    public void 虚拟内存提示_路由到内存管理页()
    {
        var vm = new CheckupPageViewModel().RunCheckup(new HealthContext(
            null, null, null, null, VirtualMemoryHint: "页面文件偏小"));
        var item = Assert.Single(vm.Findings);
        Assert.Equal(AppRoute.Mem, item.JumpRoute);
        Assert.Equal(HealthSeverity.Info, item.Severity);
    }

    [Fact]
    public void 未知动作标识_不跳转但照常展示()
    {
        var routes = new Dictionary<string, AppRoute>();                   // 空映射表
        var vm = new CheckupPageViewModel(routes).RunCheckup(Ctx(junk: 6L * 1024 * 1024 * 1024));
        var item = Assert.Single(vm.Findings);
        Assert.Null(item.JumpRoute);
        Assert.Equal("jump-disk-clean", item.SuggestedActionId);           // 原始标识保留
    }

    [Fact]
    public void 严重度主题色_三主题即时取值()
    {
        var vm = new CheckupPageViewModel();
        foreach (var theme in new[] { ThemeName.Shuimo, ThemeName.Tianqing, ThemeName.Mobai })
        {
            var tokens = ThemeTokens.Of(theme);
            Assert.Equal(tokens.Cinnabar, vm.AccentColorOf(HealthSeverity.Critical, tokens));
            Assert.Equal(tokens.Cinnabar, vm.AccentColorOf(HealthSeverity.Warning, tokens));
            Assert.Equal(tokens.Gold, vm.AccentColorOf(HealthSeverity.Advice, tokens));
            Assert.Equal(tokens.Ink2, vm.AccentColorOf(HealthSeverity.Info, tokens));
            Assert.Equal(tokens.BambooWash, vm.WashColorOf(HealthSeverity.Advice, tokens));
        }
    }

    [Fact]
    public void 无发现_满分与良好分带()
    {
        var vm = new CheckupPageViewModel().RunCheckup(Ctx(junk: 0, startup: 3, sysDisk: 60));
        Assert.Equal(100, vm.Score);
        Assert.Equal("状态很好", vm.ScoreBandText);
        Assert.Empty(vm.Findings);
    }

    // ---- R-P4-1 开始体检全链状态机（G4 初始态基准：Idle=未测态基线，本页无用户默认值） ----

    [Fact]
    public void 初始相位_Idle_未跑不出结果()
    {
        var vm = new CheckupPageViewModel();
        Assert.Equal(CheckupRunPhase.Idle, vm.RunPhase);
        Assert.Null(vm.FailureMessage);
    }

    [Fact]
    public async Task 执行器未接线_如实失败不假跑()
    {
        var vm = new CheckupPageViewModel();
        await vm.RunAsync(null);
        Assert.Equal(CheckupRunPhase.Failed, vm.RunPhase);
        Assert.Contains("尚未接线", vm.FailureMessage);
    }

    [Fact]
    public async Task 体检全链_Idle到Running到Completed_结果真实()
    {
        var vm = new CheckupPageViewModel();
        var phases = new List<CheckupRunPhase>();
        vm.PhaseChanged = phases.Add;

        await vm.RunAsync(_ => Task.FromResult(Ctx(junk: 6L * 1024 * 1024 * 1024)));
        Assert.Equal(new[] { CheckupRunPhase.Running, CheckupRunPhase.Completed }, phases);
        Assert.Equal(CheckupRunPhase.Completed, vm.RunPhase);
        Assert.False(vm.IsUntested);
        Assert.Equal(85, vm.Score); // 垃圾 6GB → Warning 15，100-15
        Assert.Null(vm.FailureMessage);
    }

    [Fact]
    public async Task 执行器抛错_转Failed_原因如实()
    {
        var vm = new CheckupPageViewModel();
        await vm.RunAsync(_ => Task.FromException<HealthContext>(new InvalidOperationException("引擎炸了")));
        Assert.Equal(CheckupRunPhase.Failed, vm.RunPhase);
        Assert.Contains("引擎炸了", vm.FailureMessage);
        Assert.Contains("只读扫描", vm.FailureMessage); // 不动文件的说明随行
    }

    [Fact]
    public async Task 执行器取消_转Failed_取消口径()
    {
        var vm = new CheckupPageViewModel();
        await vm.RunAsync(_ => Task.FromException<HealthContext>(new OperationCanceledException()));
        Assert.Equal(CheckupRunPhase.Failed, vm.RunPhase);
        Assert.Contains("取消", vm.FailureMessage);
    }

    [Fact]
    public async Task Running相位_拒绝重入()
    {
        var vm = new CheckupPageViewModel();
        var gate = new TaskCompletionSource<HealthContext>();
        var running = vm.RunAsync(_ => gate.Task); // 挂起执行器：相位停在 Running
        Assert.Equal(CheckupRunPhase.Running, vm.RunPhase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RunAsync(_ => Task.FromResult(Ctx())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => { vm.BeginRun(); return Task.CompletedTask; });

        gate.SetResult(Ctx(junk: 1024)); // 放行 → Completed
        await running;
        Assert.Equal(CheckupRunPhase.Completed, vm.RunPhase);
    }

    [Fact]
    public async Task Failed与Completed_均可再次体检()
    {
        var vm = new CheckupPageViewModel();
        await vm.RunAsync(_ => Task.FromException<HealthContext>(new InvalidOperationException("x")));
        Assert.Equal(CheckupRunPhase.Failed, vm.RunPhase);

        await vm.RunAsync(_ => Task.FromResult(Ctx(junk: 1024))); // 重跑成功
        Assert.Equal(CheckupRunPhase.Completed, vm.RunPhase);
        Assert.Null(vm.FailureMessage); // 失败原因随新跑清空
        Assert.Equal(100, vm.Score);    // 垃圾 1KB 不构成发现
    }
}
