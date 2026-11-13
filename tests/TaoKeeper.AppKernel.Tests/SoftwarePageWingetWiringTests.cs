using System;
using System.Threading.Tasks;
using TaoKeeper.Adapters;
using TaoKeeper.AppKernel.ViewModels;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>
/// 软件管家页真实链路接线测试：winget 显示格式输出（中文表头、CJK 记 2 格对齐）→ 适配层解析 →
/// Core 编排三分桶 → 页面视图模型展示。验证「对接真实清单」全链路不断点。
/// </summary>
public class SoftwarePageWingetWiringTests
{
    /// <summary>winget 真机口径：升级清单（中文表头，含一条折行条目；列位按显示格对齐，中文全角记 2 格）。</summary>
    private const string UpgradeOutput =
        "名称                  ID                版本          可用          源\r\n" +
        "----------------------------------------------------------------\r\n" +
        "网易云音乐            NetEase.Music     3.0.0         3.1.0         winget\r\n" +
        "超长名称示例软件      Long.Name.Example 1.0           2.0           winget\r\n" +
        "续文行并入名称\r\n";

    /// <summary>winget 真机口径：已安装清单（无可用列，列位与升级样例同表）。</summary>
    private const string ListOutput =
        "名称                  ID                版本          源\r\n" +
        "--------------------------------------------------\r\n" +
        "网易云音乐            NetEase.Music     3.0.0                       winget\r\n" +
        "7-Zip                 7zip.7zip         24.08                       winget\r\n";

    private static WingetSoftwareSourceAdapter WingetFake() =>
        new(args => args.StartsWith("upgrade", StringComparison.Ordinal)
            ? (0, UpgradeOutput, string.Empty)
            : (0, ListOutput, string.Empty));

    [Fact]
    public async Task winget真实格式输出_一路进页面三分桶()
    {
        var vm = await new SoftwarePageViewModel().BuildAsync(WingetFake());

        // 清册：list 输出 2 条；升级判定：upgrade 输出 2 条可升级（第 3 行为折行续文并入第 2 条名称）
        Assert.Equal(2, vm.InstalledCount);
        Assert.Equal(2, vm.TotalUpgradable);

        // winget 清单不提供哈希依据（HashVerified=null），策略默认静默关 → 全部进「需确认」桶
        Assert.Empty(vm.SilentAllowed);
        Assert.Equal(2, vm.NeedConfirm.Count);
        Assert.Empty(vm.BlockedByPolicy);

        // 动作清单：需确认项不允许静默
        Assert.Equal(2, vm.UpgradeActions.Count);
        Assert.All(vm.UpgradeActions, a => Assert.False(a.AllowSilent));

        // G7 来源收后台：用户可懂的安全状态文案真实可用，来源渠道名只留内部口径
        Assert.Equal("装前自动安全校验", SoftwarePageViewModel.SafetyText(vm.NeedConfirm[0]));
        Assert.Contains("3.1.0", SoftwarePageViewModel.VersionText(vm.NeedConfirm[0]));

        // 汇总文案按真实桶计数生成
        Assert.Contains("已安装 2 个", vm.SummaryText);
        Assert.Contains("需确认 2", vm.SummaryText);
    }

    [Fact]
    public async Task 无可升级_汇总文案为全部最新版本()
    {
        // upgrade 调用回合法表头但零数据行 → 0 条可升级
        var emptyUpgrades = UpgradeOutput.Split("\r\n")[..2];
        var adapter = new WingetSoftwareSourceAdapter(args => args.StartsWith("upgrade", StringComparison.Ordinal)
            ? (0, string.Join("\r\n", emptyUpgrades) + "\r\n", string.Empty)
            : (0, ListOutput, string.Empty));

        var vm = await new SoftwarePageViewModel().BuildAsync(adapter);

        Assert.Equal(2, vm.InstalledCount);
        Assert.Equal(0, vm.TotalUpgradable);
        Assert.False(vm.HasUpgrades);
        Assert.Contains("全部为最新版本", vm.SummaryText);
    }

    [Fact]
    public async Task winget不可用_清单读取报红透传不假空()
    {
        var adapter = new WingetSoftwareSourceAdapter(
            _ => (-1, string.Empty, "winget 命令超时，已终止；请稍后重试或改手动处理"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SoftwarePageViewModel().BuildAsync(adapter));
        Assert.Contains("超时", ex.Message);
    }
}
