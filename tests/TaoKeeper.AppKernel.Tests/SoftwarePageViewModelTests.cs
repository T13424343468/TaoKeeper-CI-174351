using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>软件管家页视图模型测试：三分桶经 Core 编排器、概览文本、来源/版本/哈希徽标文案、主题色。</summary>
public class SoftwarePageViewModelTests
{
    private static readonly SoftwareEntry WingetEntry =
        new("id.winget", "示例工具", "示例厂", "1.0.0", "1.1.0",
            SoftwareSourceKind.WingetCommunity, HashVerified: true);

    private static readonly SoftwareEntry GithubEntry =
        new("id.github", "开源工具", null, "2.0", "2.1",
            SoftwareSourceKind.GitHubRelease, HashVerified: null,
            SourceDisplay: "GitHub 官方 Releases");

    private static readonly SoftwareEntry BlockedEntry =
        new("id.blocked", "哈希失配软件", "厂", "3.0", "3.1",
            SoftwareSourceKind.OfficialPageManual, HashVerified: false);

    private static SoftwareUpgradePolicy SilentOn() => new(SilentUpgradeAllowed: true);

    [Fact]
    public async Task 策略未开静默_全部进需确认桶_概览文本含三桶计数()
    {
        var adapter = new InMemorySoftwareAdapter(
            installed: new() { WingetEntry, GithubEntry, BlockedEntry },
            upgrades: new() { WingetEntry, GithubEntry, BlockedEntry });

        var vm = await new SoftwarePageViewModel().BuildAsync(adapter);

        Assert.Equal(3, vm.InstalledCount);
        Assert.Empty(vm.SilentAllowed);
        Assert.Equal(2, vm.NeedConfirm.Count);
        Assert.Single(vm.BlockedByPolicy); // 哈希失配硬规则：不开静默也照样拦
        Assert.Equal(2, vm.UpgradeActions.Count); // 拦截项不进待执行清单
        Assert.All(vm.UpgradeActions, a => Assert.False(a.AllowSilent));
        Assert.True(vm.HasUpgrades);
        Assert.True(vm.HasBlocked);
        Assert.Equal("已安装 3 个，可升级 3 个（静默可跑 0 / 需确认 2 / 策略拦截 1）", vm.SummaryText);
    }

    [Fact]
    public async Task 开静默且哈希已过_进静默桶_允许免确认()
    {
        var adapter = new InMemorySoftwareAdapter(
            installed: new() { WingetEntry, GithubEntry },
            upgrades: new() { WingetEntry, GithubEntry });

        var vm = await new SoftwarePageViewModel().BuildAsync(adapter, SilentOn());

        Assert.Single(vm.SilentAllowed);
        Assert.Single(vm.NeedConfirm); // github 哈希未核验，开静默也需确认
        Assert.Single(vm.UpgradeActions.Where(a => a.AllowSilent));
        Assert.Contains(vm.UpgradeActions, a => a.AllowSilent && a.Software.Id == "id.winget");
        Assert.DoesNotContain(vm.UpgradeActions, a => a.AllowSilent && a.Software.Id == "id.github");
        Assert.True(vm.IsSilentAllowed(WingetEntry));
        Assert.False(vm.IsSilentAllowed(GithubEntry));
    }

    [Fact]
    public async Task 无可升级_展示全部为最新版本()
    {
        var adapter = new InMemorySoftwareAdapter(
            installed: new() { WingetEntry },
            upgrades: new List<SoftwareEntry>());

        var vm = await new SoftwarePageViewModel().BuildAsync(adapter, SilentOn());

        Assert.False(vm.HasUpgrades);
        Assert.False(vm.HasBlocked);
        Assert.Empty(vm.UpgradeActions);
        Assert.Equal("已安装 1 个，全部为最新版本", vm.SummaryText);
    }

    [Fact]
    public async Task 拦截项_朱砂色且徽标标死_不放行()
    {
        var adapter = new InMemorySoftwareAdapter(
            installed: new() { BlockedEntry },
            upgrades: new() { BlockedEntry });

        var vm = await new SoftwarePageViewModel().BuildAsync(adapter, SilentOn());
        var tokens = ThemeTokens.Default;

        Assert.False(vm.IsSilentAllowed(BlockedEntry));
        Assert.Equal(tokens.Cinnabar, vm.StatusColorOf(BlockedEntry, tokens));
        Assert.Equal("安全校验未过 · 已拦截", SoftwarePageViewModel.SafetyText(BlockedEntry));
        // 拦截项绝不出现在任何待执行动作里
        Assert.DoesNotContain(vm.UpgradeActions, a => a.Software.Id == "id.blocked");
    }

    [Fact]
    public void 文案回退_来源缺省按渠道_版本箭头_未核验徽标()
    {
        Assert.Equal("winget 社区源", SoftwarePageViewModel.SourceText(WingetEntry));
        Assert.Equal("GitHub 官方 Releases", SoftwarePageViewModel.SourceText(GithubEntry)); // 优先适配器说明
        var manual = new SoftwareEntry("m", "官网软件", null, null, null, SoftwareSourceKind.OfficialPageManual);
        Assert.Equal("官网直下（手动确认）", SoftwarePageViewModel.SourceText(manual));

        Assert.Equal("1.0.0 → 1.1.0", SoftwarePageViewModel.VersionText(WingetEntry));
        Assert.Equal("未知版本", SoftwarePageViewModel.VersionText(manual));

        // G7 用户侧口径：来源渠道名不外显，安全状态说人话
        Assert.Equal("安全校验已通过", SoftwarePageViewModel.SafetyText(WingetEntry));
        Assert.Equal("装前自动安全校验", SoftwarePageViewModel.SafetyText(GithubEntry));
        Assert.Equal("装前自动安全校验", SoftwarePageViewModel.SafetyText(manual));
    }

    [Fact]
    public void 未扫描初始态_固定文案且零动作()
    {
        var vm = new SoftwarePageViewModel();
        Assert.Equal("尚未扫描软件清单", vm.SummaryText);
        Assert.False(vm.HasUpgrades);
        Assert.Empty(vm.UpgradeActions);
    }
}
