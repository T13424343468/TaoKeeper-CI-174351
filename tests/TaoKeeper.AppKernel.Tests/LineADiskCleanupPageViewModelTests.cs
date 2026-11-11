using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>
/// LineA·磁盘清理页视图模型测试：规则聚合（体积降序）、默认勾选风险口径（高风险不勾）、
/// 勾选变化即时重算汇总与确认队列、闸门拒绝项标死、未扫描态如实、体积分档边界。
/// </summary>
public class LineADiskCleanupPageViewModelTests
{
    private static JunkRule Rule(string id, string title, JunkRisk risk) =>
        new(id, title, $"{title} 描述", new[] { "%TEMP%" }, null, null, risk);

    private static JunkCandidate Cand(JunkRule rule, string path, long size) =>
        new(rule, path, size, DateTimeOffset.UtcNow, "命中规则");

    [Fact]
    public void 未扫描态_全部如实不假数据()
    {
        var vm = new DiskCleanupPageViewModel();

        Assert.False(vm.HasScan);
        Assert.Equal("尚未扫描", vm.TotalText);
        Assert.Equal("磁盘容量待扫描后显示", vm.VolumeText);
        Assert.Empty(vm.Rows);
        Assert.Null(vm.Queue);
        Assert.Equal("已选 0 项，合计 0 B", vm.SelectedSummaryText);
        Assert.Empty(vm.BuildConfirmedActions());
    }

    [Fact]
    public void 规则聚合_体积降序_默认勾选低中风险_高风险不勾()
    {
        var low = Rule("low-r", "低风险规则", JunkRisk.Low);
        var high = Rule("high-r", "高风险规则", JunkRisk.High);
        var scan = new JunkScanResult(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new[]
            {
                Cand(high, "C:\\h1", 400), Cand(high, "C:\\h2", 100),  // 高风险 500，默认不勾
                Cand(low, "C:\\l1", 1_200_000),                        // 低风险 1.2 MB，默认勾
            },
            1_200_500);

        var vm = new DiskCleanupPageViewModel().Build(scan);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("low-r", vm.Rows[0].RuleId);                    // 1.2 MB 在前
        Assert.True(vm.Rows[0].IsChecked);
        Assert.False(vm.Rows[1].IsChecked);                          // 高风险默认不勾
        Assert.Equal("1.1 MB", vm.Rows[0].SizeText);
        Assert.Equal(2, vm.Rows[1].FileCount);                       // 高风险两条并一行
        Assert.Equal(1, vm.SelectedRuleCount);                       // 仅低风险默认勾
        Assert.Equal(1_200_000, vm.SelectedBytes);
        Assert.Equal("已选 1 项，合计 1.1 MB", vm.SelectedSummaryText);
        Assert.Equal(1, vm.Queue!.ConfirmedCount);                   // 队列只含勾选规则的候选
    }

    [Fact]
    public void 勾选变化_汇总与队列即时重建_取消后动作清单随之缩()
    {
        var r1 = Rule("r1", "规则一", JunkRisk.Low);
        var r2 = Rule("r2", "规则二", JunkRisk.Medium);
        var scan = new JunkScanResult(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new[] { Cand(r1, "C:\\a", 100), Cand(r1, "C:\\b", 50), Cand(r2, "C:\\c", 200) },
            350);

        var vm = new DiskCleanupPageViewModel().Build(scan);
        Assert.Equal(2, vm.SelectedRuleCount);
        Assert.Equal(350, vm.SelectedBytes);
        Assert.Equal(3, vm.Queue!.ConfirmedCount);

        vm.ToggleRule("r2", false);
        Assert.Equal(1, vm.SelectedRuleCount);
        Assert.Equal(150, vm.SelectedBytes);
        Assert.Equal(2, vm.Queue.ConfirmedCount);
        Assert.Equal(2, vm.BuildConfirmedActions().Count);

        vm.ToggleRule("r1", false);
        Assert.Equal(0, vm.SelectedRuleCount);
        Assert.Empty(vm.BuildConfirmedActions());
    }

    [Fact]
    public void 闸门预检_受保护路径标死不可勾_不进动作清单()
    {
        var rule = Rule("r", "规则", JunkRisk.Low);
        var scan = new JunkScanResult(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new[] { Cand(rule, "C:\\Windows\\System32\\config\\x", 999), Cand(rule, "C:\\ok.tmp", 10) },
            1009);

        var vm = new DiskCleanupPageViewModel().Build(scan);

        Assert.Equal(2, vm.Queue!.Items.Count);
        Assert.NotNull(vm.Queue.Items.First(i => i.FullPath.Contains("System32")).PathRefusal); // 源头标死
        Assert.False(vm.Queue.Items.First(i => i.FullPath.Contains("System32")).CanConfirm);
        Assert.False(vm.Queue.Items.First(i => i.FullPath.Contains("System32")).Confirmed);     // 拒绝项勾不上
        var actions = vm.BuildConfirmedActions();
        Assert.Single(actions);                       // 拒绝项根本进不了执行清单
        Assert.Equal("C:\\ok.tmp", actions[0].FullPath);
    }

    [Fact]
    public void 零候选_很干净口径_容量信息可选注入()
    {
        var scan = new JunkScanResult(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Array.Empty<JunkCandidate>(), 0);

        var vm = new DiskCleanupPageViewModel().Build(scan, volume: (512L * 1024 * 1024 * 1024, 341L * 1024 * 1024 * 1024));

        Assert.Equal("很干净，暂无可清", vm.TotalText);
        Assert.Empty(vm.Rows);
        Assert.Equal("磁盘共 512 GB · 已用 341 GB", vm.VolumeText);
    }

    [Fact]
    public void 体积分档_字节到GB边界()
    {
        Assert.Equal("512 B", DiskCleanupPageViewModel.Fmt(512));
        Assert.Equal("1 KB", DiskCleanupPageViewModel.Fmt(1024));
        Assert.Equal("1.5 MB", DiskCleanupPageViewModel.Fmt(1536 * 1024));
        Assert.Equal("3.2 GB", DiskCleanupPageViewModel.Fmt(3_400_000_000));
        Assert.Equal("0 B", DiskCleanupPageViewModel.Fmt(-1));
    }

    [Fact]
    public void 未知规则与未扫描_切换安全忽略()
    {
        var vm = new DiskCleanupPageViewModel();
        vm.ToggleRule("ghost", true);                 // 未扫描：忽略不抛
        Assert.Null(vm.Queue);

        var rule = Rule("r", "规则", JunkRisk.Low);
        vm.Build(new JunkScanResult(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new[] { Cand(rule, "C:\\a", 1) }, 1));
        vm.ToggleRule("ghost", true);                 // 未知规则：忽略不抛
        Assert.Equal(1, vm.SelectedRuleCount);
    }
}
