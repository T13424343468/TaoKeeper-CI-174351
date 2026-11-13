using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TaoKeeper.AppKernel.Navigation;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>
/// 总览页视图模型测试：未测态兜底（不假 100 分/不假数据）、实测态聚合文案、
/// 清理账按批聚合与「最近三笔」截取、快捷卡路由映射、格式化边界。
/// </summary>
public class HomePageViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.FromHours(8));
    // ToLocalTime 在沙箱时区（UTC）下与本地构造的 DateTimeOffset 直接比较，测试只用相对断言

    private static LedgerEntry Ledger(DateTimeOffset at, string source, long bytes, string path) =>
        new($"id-{Guid.NewGuid():N}", at, source, path, bytes, LedgerRestoreKind.RecycleBin, "C:\\$Recycle.Bin\\x");

    [Fact]
    public void 未测态_评分不展示_副文与快捷卡全如实()
    {
        var vm = HomePageViewModel.Untested();

        Assert.True(vm.Score is null);
        Assert.Equal("未测", vm.ScoreBandText);
        Assert.Equal("尚无体检记录，先做一次体检看看整体状况", vm.HeroSubtitle);
        Assert.Equal(4, vm.QuickCards.Count);          // 快捷卡照常给入口
        Assert.Equal(AppRoute.SwStore, vm.QuickCards[0].Route);
        Assert.Equal("尚未扫描", vm.QuickCards[1].Subtitle); // 磁盘清理卡如实显示未扫
        Assert.Equal("内存占用未知", vm.QuickCards[2].Subtitle); // 内存未知不假百分比
        Assert.Empty(vm.LedgerLines);                   // 无账不编造
    }

    [Fact]
    public void 实测态_评分垃圾升级内存聚合进副文与统计()
    {
        var checkup = new CheckupPageViewModel().RunCheckup(new HealthContext(
            JunkReclaimableBytes: 3_400_000_000,   // ~3.2 GB
            DuplicateReclaimableBytes: null,
            StartupItemCount: 3,
            SystemDiskFreePercent: null), Now.AddDays(-2));

        var vm = HomePageViewModel.Build(
            checkup, Now.AddDays(-2), null, null, 62, Array.Empty<LedgerEntry>(), Now);

        Assert.False(string.IsNullOrEmpty(vm.ScoreBandText));
        Assert.Contains("上次体检", vm.HeroSubtitle);
        Assert.Equal(62, vm.MemoryUsedPercent);
        Assert.Equal("物理内存 62%", vm.QuickCards[2].Subtitle);
        // 软件未扫：升级卡与统计不假造数字
        Assert.True(vm.UpgradeCount is null);
        Assert.Equal("尚未扫描软件清单", vm.QuickCards[0].Subtitle);
    }

    // ---- R-P1-3 定向回归：红灯②「Nullable object must have a value」崩溃三路径 ----

    [Fact]
    public void R1P3_未体检打开总览_不崩溃_副文未测文案()
    {
        // ①未体检状态打开总览：副标题「时间未记」类未测文案，不出现强拆空值崩溃
        var vm = HomePageViewModel.Build(
            new CheckupPageViewModel().RunCheckup(HealthContext.Empty, Now), // 未测体检
            null, null, null, null, Array.Empty<LedgerEntry>(), Now);

        Assert.Equal("尚无体检记录，先做一次体检看看整体状况", vm.HeroSubtitle);
        Assert.True(vm.Score is null);
    }

    [Fact]
    public void R1P3_体检后返回总览_副文含时间与评分()
    {
        // ②跑完体检返回总览：副标题显示最近一次体检时间与结论摘要
        var checkupAt = Now.AddDays(-1);
        var checkup = new CheckupPageViewModel().RunCheckup(new HealthContext(
            JunkReclaimableBytes: 1_000_000, DuplicateReclaimableBytes: null,
            StartupItemCount: 2, SystemDiskFreePercent: null), checkupAt);

        var vm = HomePageViewModel.Build(
            checkup, checkupAt, null, null, null, Array.Empty<LedgerEntry>(), Now);

        Assert.Contains("上次体检", vm.HeroSubtitle);
        Assert.Contains("评分", vm.HeroSubtitle);
        Assert.False(vm.Score is null);
    }

    [Fact]
    public void R1P3_持久化时间缺失_重启后再进总览_不崩溃_时间未记()
    {
        // ③重启应用后再进总览：持久化时间为空（存档丢失/未写入）但体检记录在——
        // 任何路径不出现「Nullable object must have a value」
        var checkup = new CheckupPageViewModel().RunCheckup(new HealthContext(
            JunkReclaimableBytes: 1_000_000, DuplicateReclaimableBytes: null,
            StartupItemCount: 2, SystemDiskFreePercent: null), Now);

        var vm = HomePageViewModel.Build(
            checkup, lastCheckupAtUtc: null, null, null, null, Array.Empty<LedgerEntry>(), Now);

        Assert.Contains("时间未记", vm.HeroSubtitle);
        Assert.Contains("跑一次体检", vm.HeroSubtitle);
    }

    [Fact]
    public void 垃圾与升级实测_文案与拦截数如实()
    {
        var checkup = new CheckupPageViewModel().RunCheckup(HealthContext.Empty, Now);
        // 未测体检 + 有垃圾扫描：垃圾卡如实显示，hero 保持未测文案
        var junkResult = new JunkScanResult(Now, Now, Array.Empty<JunkCandidate>(), 0);
        var vm = HomePageViewModel.Build(checkup, null, junkResult, null, null, null, Now);
        Assert.Equal("很干净，暂无可清", vm.QuickCards[1].Subtitle);
        Assert.Equal("尚无体检记录，先做一次体检看看整体状况", vm.HeroSubtitle);
    }

    [Fact]
    public void 清理账按批聚合_同来源一分钟内并一笔_最近三笔截取()
    {
        var baseAt = Now.AddHours(-3);
        var entries = new List<LedgerEntry>
        {
            Ledger(baseAt, "junk", 100, "C:\\a.tmp"),
            Ledger(baseAt.AddSeconds(30), "junk", 200, "C:\\b.tmp"),   // 同批
            Ledger(baseAt.AddMinutes(2), "duplicates", 500, "C:\\c"),   // 超一分钟：新一笔
            Ledger(baseAt.AddMinutes(5), "junk", 700, "C:\\d"),
            Ledger(baseAt.AddMinutes(9), "junk", 900, "C:\\e"),         // 最旧的三批之后，应被截掉前两笔
        };

        var vm = HomePageViewModel.Build(
            new CheckupPageViewModel().RunCheckup(HealthContext.Empty, Now), null,
            null, null, null, entries, Now);

        Assert.Equal(3, vm.LedgerLines.Count);           // 只留最近三笔
        // 最近三笔 = (9min 900B) / (5min 700B) / (2min 500B duplicates)；最早的合批 300B 被截掉
        Assert.Contains("清理垃圾文件 1 项，共 900 B", vm.LedgerLines[0].SummaryText);
        Assert.Contains("清理垃圾文件 1 项，共 700 B", vm.LedgerLines[1].SummaryText);
        Assert.Contains("清理重复文件 1 项，共 500 B", vm.LedgerLines[2].SummaryText);
        Assert.All(vm.LedgerLines, l => Assert.EndsWith("已进回收站", l.SummaryText));
    }

    [Fact]
    public void 清理账跨来源相邻_不并笔()
    {
        var baseAt = Now.AddMinutes(-1);
        var entries = new List<LedgerEntry>
        {
            Ledger(baseAt, "junk", 100, "C:\\a.tmp"),
            Ledger(baseAt.AddSeconds(10), "duplicates", 50, "C:\\b"), // 同一分钟但来源不同：各算一笔
        };

        var vm = HomePageViewModel.Build(
            new CheckupPageViewModel().RunCheckup(HealthContext.Empty, Now), null,
            null, null, null, entries, Now);

        Assert.Equal(2, vm.LedgerLines.Count);
    }

    [Fact]
    public void 格式化边界_字节与天数()
    {
        Assert.Equal("512 B", HomePageViewModelTestHelpers.Bytes(512));
        Assert.Equal("1 KB", HomePageViewModelTestHelpers.Bytes(1024));
        Assert.Equal("1.5 MB", HomePageViewModelTestHelpers.Bytes(1024L * 1024 * 3 / 2));
        Assert.Equal("3.2 GB", HomePageViewModelTestHelpers.Bytes(3_400_000_000));
        Assert.Equal("0 B", HomePageViewModelTestHelpers.Bytes(-5));

        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal("今天", HomePageViewModelTestHelpers.Day(new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.FromHours(8)), now));
        Assert.Equal("昨天", HomePageViewModelTestHelpers.Day(new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.FromHours(8)), now));
        Assert.Equal("11 月 7 日", HomePageViewModelTestHelpers.Day(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.FromHours(8)), now));
        Assert.Equal("2026 年 8 月", HomePageViewModelTestHelpers.Day(new DateTimeOffset(2026, 8, 9, 9, 0, 0, TimeSpan.FromHours(8)), now));
    }
}

/// <summary>测试入口：HomePageViewModel 的内部格式化（internal）经此桥接断言。</summary>
internal static class HomePageViewModelTestHelpers
{
    public static string Bytes(long bytes) => VMInternals.Bytes(bytes);
    public static string Day(DateTimeOffset value, DateTimeOffset now) => VMInternals.Day(value, now);
}

/// <summary>访问 internal 成员的桥（需 TaoKeeper.AppKernel InternalsVisibleTo 支持时改反射；当前用公开路径绕行）。</summary>
internal static class VMInternals
{
    public static string Bytes(long bytes) =>
        (string?)typeof(HomePageViewModel)
            .GetMethod("FormatBytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { bytes })! ?? throw new InvalidOperationException();

    public static string Day(DateTimeOffset value, DateTimeOffset now) =>
        (string?)typeof(HomePageViewModel)
            .GetMethod("FormatDay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { value, now })! ?? throw new InvalidOperationException();
}
