using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TaoKeeper.Adapters;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Adapters.Tests;

/// <summary>winget 表格输出解析器测试：英文/中文表头、显示格对齐、可用列缺失、续文行、占位值、未知表头报红。</summary>
public class WingetOutputParserTests
{
    /// <summary>按显示格对齐的样例（中文全角记 2 格，模拟真实控制台对齐）。</summary>
    private const string EnglishUpgradeOutput =
        "Name                          Id                    Version      Available    Source\r\n" +
        "------------------------------------------------------------------------------------\r\n" +
        "7-Zip                         7zip.7zip             24.08        24.09        winget\r\n" +
        "Git                           Git.Git               2.47.0                    winget\r\n" +
        "Some Very Long Name That Wrapssome.id               1.0          2.0          winget\r\n" +
        "Across Lines Continuation\r\n";

    private const string ChineseUpgradeOutput =
        "名称                ID                版本        可用        源\r\n" +
        "----------------------------------------------------------------\r\n" +
        "网易云音乐          NetEase.Music     3.0.0       3.1.0       winget\r\n";

    private const string EnglishListOutput =
        "Name                              Id                    Version       Source\r\n" +
        "----------------------------------------------------------------------------\r\n" +
        "Everything                        voidtools.Everything  1.4.1         winget\r\n";

    [Fact]
    public void 英文表头_解析版本与可用列_占位回空()
    {
        var rows = WingetOutputParser.ParseUpgrades(EnglishUpgradeOutput);

        Assert.Equal(3, rows.Count);
        Assert.Equal("7zip.7zip", rows[0].Id);
        Assert.Equal("24.08", rows[0].InstalledVersion);
        Assert.Equal("24.09", rows[0].AvailableVersion);
        Assert.Equal("winget 社区源", rows[0].SourceDisplay);
        Assert.Null(rows[1].AvailableVersion); // Available 空白=无升级信息，回 null
        Assert.Equal("Git", rows[1].Name);
        // 折行续文并入上一行名称
        Assert.Equal("Some Very Long Name That WrapsAcross Lines Continuation", rows[2].Name);
    }

    [Fact]
    public void 中文表头_按显示格对齐同样解析()
    {
        var rows = WingetOutputParser.ParseUpgrades(ChineseUpgradeOutput);

        Assert.Single(rows);
        Assert.Equal("NetEase.Music", rows[0].Id);
        Assert.Equal("网易云音乐", rows[0].Name);
        Assert.Equal("3.0.0", rows[0].InstalledVersion);
        Assert.Equal("3.1.0", rows[0].AvailableVersion);
    }

    [Fact]
    public void list输出_可用列缺失时不误判升级()
    {
        var rows = WingetOutputParser.ParseList(EnglishListOutput);

        Assert.Single(rows);
        Assert.Equal("voidtools.Everything", rows[0].Id);
        Assert.Equal("1.4.1", rows[0].InstalledVersion);
        Assert.Null(rows[0].AvailableVersion);
    }

    [Fact]
    public void 有文本但表头认不出_报红不按空清单处理()
    {
        Assert.Throws<FormatException>(() => WingetOutputParser.ParseUpgrades("The following packages need upgrade:\r\n"));
    }

    [Fact]
    public void 完全空输出_合法空清单不报错()
    {
        Assert.Empty(WingetOutputParser.ParseUpgrades(string.Empty));
        Assert.Empty(WingetOutputParser.ParseUpgrades("\r\n   \r\n"));
    }

    [Fact]
    public void 显示宽度_CJK按两格计算()
    {
        Assert.Equal(4, WingetOutputParser.DisplayWidth("名称"));
        Assert.Equal(10, WingetOutputParser.DisplayWidth("网易云音乐"));
        Assert.Equal(13, WingetOutputParser.DisplayWidth("NetEase.Music"));
    }
}

/// <summary>winget 适配器测试：注入假 Runner 验证参数拼装、哈希硬规则、结果映射。</summary>
public class WingetSoftwareSourceAdapterTests
{
    private static readonly SoftwareEntry Verified = new(
        "7zip.7zip", "7-Zip", null, "24.08", "24.09", SoftwareSourceKind.WingetCommunity, HashVerified: true);

    private static readonly SoftwareEntry Mismatched = new(
        "bad.pkg", "Bad Pkg", null, "1.0", "2.0", SoftwareSourceKind.WingetCommunity, HashVerified: false);

    [Fact]
    public async Task 哈希失配_不进winget直接拒绝_文案为标准拒绝文案()
    {
        int calls = 0;
        WingetSoftwareSourceAdapter.Runner fake = _ => { calls++; return (0, string.Empty, string.Empty); };
        var adapter = new WingetSoftwareSourceAdapter(fake);

        var result = await adapter.InstallAsync(Mismatched, allowSilent: true);

        Assert.Equal(0, calls); // 硬规则：确认也不放行，winget 根本不被调用
        Assert.False(result.Success);
        Assert.False(result.HashVerified);
        Assert.Equal(SoftwareActionResult.HashMismatchDetail, result.Detail);
    }

    [Fact]
    public async Task 成功升级_静默开关进参数_哈希回真()
    {
        var seen = new List<string>();
        WingetSoftwareSourceAdapter.Runner fake = args => { seen.Add(args); return (0, "成功安装", string.Empty); };
        var adapter = new WingetSoftwareSourceAdapter(fake);

        var loud = await adapter.InstallAsync(Verified, allowSilent: false);
        var silent = await adapter.InstallAsync(Verified, allowSilent: true);

        Assert.True(loud.Success);
        Assert.True(loud.HashVerified);
        Assert.Contains("--accept-package-agreements", seen[0]);
        Assert.DoesNotContain("--silent", seen[0]);
        Assert.True(silent.Success);
        Assert.Contains("--silent", seen[1]);
        Assert.Contains("--source winget", seen[1]);
        Assert.Contains("--source winget", seen[0]);
    }

    [Fact]
    public async Task 失败输出含哈希标记_哈希回假_不含则回未核验()
    {
        WingetSoftwareSourceAdapter.Runner hashFail = _ => (0x7A150001, string.Empty, "Installer hash verification failed");
        var adapter = new WingetSoftwareSourceAdapter(hashFail);
        var failed = await adapter.InstallAsync(Verified, allowSilent: true);
        Assert.False(failed.Success);
        Assert.False(failed.HashVerified);
        Assert.Contains("hash", failed.Detail, StringComparison.OrdinalIgnoreCase);

        WingetSoftwareSourceAdapter.Runner plainFail = _ => (1, string.Empty, "No package found matching input criteria.");
        var adapter2 = new WingetSoftwareSourceAdapter(plainFail);
        var failed2 = await adapter2.InstallAsync(Verified, allowSilent: true);
        Assert.False(failed2.Success);
        Assert.Null(failed2.HashVerified);
    }

    [Fact]
    public async Task 清单读取_走upgrade命令并解析()
    {
        string? seen = null;
        WingetSoftwareSourceAdapter.Runner fake = args =>
        {
            seen = args;
            return (0,
                "Name                          Id                    Version      Available    Source\r\n" +
                "------------------------------------------------------------------------------------\r\n" +
                "7-Zip                         7zip.7zip             24.08        24.09        winget\r\n",
                string.Empty);
        };
        var adapter = new WingetSoftwareSourceAdapter(fake);

        var upgrades = await adapter.ListUpgradesAsync();

        Assert.NotNull(seen);
        Assert.Contains("upgrade", seen);
        Assert.Contains("--include-unknown", seen);
        var only = Assert.Single(upgrades);
        Assert.Equal("7zip.7zip", only.Id);
        Assert.Equal("24.09", only.AvailableVersion);
    }

    [Fact]
    public async Task 危险Id拒绝_不进winget()
    {
        int calls = 0;
        WingetSoftwareSourceAdapter.Runner fake = _ => { calls++; return (0, string.Empty, string.Empty); };
        var adapter = new WingetSoftwareSourceAdapter(fake);
        var evil = new SoftwareEntry("id\"; rm -rf /", "Evil", null, "1.0", "2.0",
            SoftwareSourceKind.WingetCommunity, HashVerified: true);

        Assert.False(WingetSoftwareSourceAdapter.IsSafeId("id\"; rm -rf /"));
        Assert.True(WingetSoftwareSourceAdapter.IsSafeId("7zip.7zip"));
        _ = await adapter.InstallAsync(evil, allowSilent: true);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void 非Windows真Runner报红不静默()
    {
        if (OperatingSystem.IsWindows()) return; // 真机跑测试时不触发
        var runner = WingetSoftwareSourceAdapter.MakeRealRunner();
        Assert.Throws<PlatformNotSupportedException>(() => runner("list"));
    }

    [Fact]
    public async Task 负退出码_清单读取报错不返回假空清单()
    {
        WingetSoftwareSourceAdapter.Runner fake = _ => (-1, string.Empty, "winget 命令超时，已终止；请稍后重试或改手动处理");
        var adapter = new WingetSoftwareSourceAdapter(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ListUpgradesAsync());
    }

    [Fact]
    public async Task 表头未识别_清单读取报红不假空()
    {
        WingetSoftwareSourceAdapter.Runner fake = _ => (0, "The following packages need upgrade:", string.Empty);
        var adapter = new WingetSoftwareSourceAdapter(fake);
        await Assert.ThrowsAsync<FormatException>(() => adapter.ListUpgradesAsync());
    }

    [Fact]
    public async Task 已取消令牌_不进winget不执行()
    {
        int calls = 0;
        WingetSoftwareSourceAdapter.Runner fake = _ => { calls++; return (0, string.Empty, string.Empty); };
        var adapter = new WingetSoftwareSourceAdapter(fake);
        var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ListUpgradesAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.InstallAsync(Verified, allowSilent: true, cts.Token));

        Assert.Equal(0, calls); // 令牌先查后跑，取消态根本不触达 winget
    }

    [Fact]
    public async Task 后台线程执行_完成后LastArguments跨线程可见()
    {
        WingetSoftwareSourceAdapter.Runner fake = _ => (0, "成功安装", string.Empty);
        var adapter = new WingetSoftwareSourceAdapter(fake);

        await adapter.InstallAsync(Verified, allowSilent: true);

        Assert.NotNull(adapter.LastArguments);   // 进程调用在后台线程完成，回读可见（volatile 保证）
        Assert.Contains("--silent", adapter.LastArguments);
        Assert.Contains(Verified.Id, adapter.LastArguments);
    }
}
