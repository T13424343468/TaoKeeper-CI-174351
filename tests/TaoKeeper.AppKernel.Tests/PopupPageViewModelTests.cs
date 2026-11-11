using System;
using System.Linq;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>弹窗管理页视图模型测试：规则开关、白名单优先、留痕回溯、环形截取与配置报红。</summary>
public class PopupPageViewModelTests
{
    private static PopupEvent Ev(string process = "广告进程", string title = "限时特惠", string cls = "Popup") =>
        new(process, title, cls);

    private static PopupRule Rule(string id = "r1", bool enabled = true) =>
        new(id, "测试规则", PopupMatchKind.ProcessName, "广告进程", enabled);

    [Fact]
    public void 规则命中拦截_未命中放行_全程留痕()
    {
        var vm = new PopupPageViewModel(new[] { Rule() });
        Assert.Equal(PopupAction.Blocked, vm.Evaluate(Ev()).Action);
        Assert.Equal(PopupAction.Allowed, vm.Evaluate(Ev("普通进程")).Action);
        Assert.Equal(1, vm.BlockedCount);
        Assert.Equal(1, vm.AllowedCount);
        Assert.Equal(2, vm.RecentRecords.Count);
    }

    [Fact]
    public void 白名单优先于规则_关闭规则不拦()
    {
        var vm = new PopupPageViewModel(
            new[] { Rule() },
            new[] { new PopupWhitelistEntry("w1", PopupMatchKind.ProcessName, "广告进程", "常用地推弹窗") });
        Assert.Equal(PopupAction.Whitelisted, vm.Evaluate(Ev()).Action);   // 白名单优先，规则即使命中也轮不到
        Assert.Equal(1, vm.WhitelistedCount);
        Assert.Equal(0, vm.BlockedCount);

        vm.RemoveWhitelist("w1");
        vm.SetRuleEnabled("r1", false);
        Assert.Equal(PopupAction.Allowed, vm.Evaluate(Ev()).Action);       // 规则关了、白名单删了 → 放行
    }

    [Fact]
    public void 规则增删改查与重复Id报红()
    {
        var vm = new PopupPageViewModel();
        vm.AddRule(Rule("a"));
        vm.AddRule(Rule("b", enabled: false));
        Assert.Equal(2, vm.Rules.Count);

        Assert.Throws<InvalidOperationException>(() => vm.AddRule(Rule("a")));
        vm.SetRuleEnabled("b", true);
        Assert.True(vm.Rules.Single(r => r.Id == "b").Enabled);
        vm.RemoveRule("a");
        Assert.Single(vm.Rules);
        Assert.Throws<KeyNotFoundException>(() => vm.RemoveRule("a"));
    }

    [Fact]
    public void 白名单增删与重复Id报红()
    {
        var vm = new PopupPageViewModel();
        vm.AddWhitelist(new PopupWhitelistEntry("w1", PopupMatchKind.WindowClass, "SafeClass"));
        Assert.Throws<InvalidOperationException>(() =>
            vm.AddWhitelist(new PopupWhitelistEntry("w1", PopupMatchKind.WindowClass, "X")));
        vm.RemoveWhitelist("w1");
        Assert.Empty(vm.Whitelist);
        Assert.Throws<KeyNotFoundException>(() => vm.RemoveWhitelist("w1"));
    }

    [Fact]
    public void 回溯记录环形截取_最新在前()
    {
        var vm = new PopupPageViewModel(new[] { Rule() }) { RecentLimit = 3 };
        for (var i = 0; i < 5; i++)
            vm.Evaluate(Ev());
        Assert.Equal(3, vm.RecentRecords.Count);
        Assert.True(vm.RecentRecords[0].OccurredUtc >= vm.RecentRecords[^1].OccurredUtc);
    }

    [Fact]
    public void 汇总文案与回溯描述转述()
    {
        var vm = new PopupPageViewModel(new[] { Rule() });
        vm.Evaluate(Ev());
        Assert.Contains("拦截 1", vm.SummaryText);
        var text = PopupPageViewModel.Describe(vm.RecentRecords[0]);
        Assert.Contains("已拦截", text);
        Assert.Contains("规则 r1", text);
    }

    [Fact]
    public void 空参数与空对象一律报红()
    {
        var vm = new PopupPageViewModel();
        Assert.Throws<ArgumentNullException>(() => vm.Evaluate(null!));
        Assert.Throws<ArgumentException>(() => vm.SetRuleEnabled(" ", true));
        Assert.Throws<ArgumentNullException>(() => vm.AddRule(null!));
        Assert.Throws<ArgumentNullException>(() => vm.AddWhitelist(null!));
    }
}
