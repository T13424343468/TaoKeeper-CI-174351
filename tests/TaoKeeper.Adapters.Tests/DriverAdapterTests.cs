using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TaoKeeper.Adapters;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Adapters.Tests;

/// <summary>pnputil /enum-drivers 解析器测试：英文/中文标签、分块结构、说明行跳过、错误输出报红。</summary>
public class PnputilOutputParserTests
{
    private const string EnglishOutput =
        "Microsoft PnP Utility\r\n" +
        "\r\n" +
        "Published Name:            oem12.inf\r\n" +
        "Original Name:             nvlt.inf\r\n" +
        "Provider Name:             NVIDIA\r\n" +
        "Class Name:                Display adapters\r\n" +
        "Class GUID:                {4d36e968-e325-11ce-bfc1-08002be10318}\r\n" +
        "Driver Version:            06/14/2021 27.21.14.5282\r\n" +
        "Driver Date:               06/14/2021\r\n" +
        "\r\n" +
        "Published Name:            oem7.inf\r\n" +
        "Original Name:             iigc.inf\r\n" +
        "Provider Name:             Intel\r\n" +
        "Class Name:                Net\r\n" +
        "Class GUID:                {4d36e972-e325-11ce-bfc1-08002be10318}\r\n" +
        "Driver Version:            03/02/2022 12.1.2.3\r\n" +
        "Driver Date:               03/02/2022\r\n";

    private const string ChineseOutput =
        "Microsoft PnP 工具\r\n" +
        "\r\n" +
        "发布的名称:            oem12.inf\r\n" +
        "原始名称:              nvlt.inf\r\n" +
        "提供程序名称:          NVIDIA\r\n" +
        "类名:                  显示适配器\r\n" +
        "驱动程序版本:          06/14/2021 27.21.14.5282\r\n" +
        "驱动程序日期:          06/14/2021\r\n";

    [Fact]
    public void 英文标签_解析多驱动块()
    {
        var rows = PnputilOutputParser.ParseEnumDrivers(EnglishOutput);

        Assert.Equal(2, rows.Count);
        Assert.Equal("oem12.inf", rows[0].PublishedName);
        Assert.Equal("NVIDIA", rows[0].Provider);
        Assert.Equal("Display adapters", rows[0].ClassName);
        Assert.Contains("27.21.14.5282", rows[0].Version);
        Assert.Equal("Net", rows[1].ClassName);
    }

    [Fact]
    public void 中文标签_同样解析()
    {
        var rows = PnputilOutputParser.ParseEnumDrivers(ChineseOutput);

        Assert.Single(rows);
        Assert.Equal("oem12.inf", rows[0].PublishedName);
        Assert.Equal("显示适配器", rows[0].ClassName);
    }

    [Fact]
    public void 空输出_合法不报错()
    {
        Assert.Empty(PnputilOutputParser.ParseEnumDrivers(string.Empty));
        Assert.Empty(PnputilOutputParser.ParseEnumDrivers("Microsoft PnP Utility\r\n"));
    }

    [Fact]
    public void 错误输出_无任何标签_报红不按空清单()
    {
        Assert.Throws<FormatException>(() => PnputilOutputParser.ParseEnumDrivers("Failed to enumerate drivers 0x80070005"));
    }

    [Fact]
    public void 高危类判定_显卡芯片组存储网络命中_其他不误伤()
    {
        Assert.True(DriverCriticality.IsCriticalClass("Display adapters"));
        Assert.True(DriverCriticality.IsCriticalClass("显示适配器"));
        Assert.True(DriverCriticality.IsCriticalClass("Net"));
        Assert.True(DriverCriticality.IsCriticalClass("SCSIAdapter"));
        Assert.True(DriverCriticality.IsCriticalClass("System"));
        Assert.False(DriverCriticality.IsCriticalClass("打印机"));
        Assert.False(DriverCriticality.IsCriticalClass(null));
    }
}

/// <summary>Windows 驱动适配器测试：注入假 WUA 网关与假 pnputil，验证三道闸衔接与结果映射。</summary>
public class WindowsDriverUpdateAdapterTests
{
    private sealed class FakeWuaGateway : IWuaDriverGateway
    {
        public List<IWuaDriverGateway.WuaDriver> Drivers { get; set; } = new();
        public bool InstallSucceeds { get; set; } = true;
        public int InstallCalls { get; private set; }

        public Task<IReadOnlyList<IWuaDriverGateway.WuaDriver>> SearchDriverUpdatesAsync(System.Threading.CancellationToken cancellation = default)
            => Task.FromResult((IReadOnlyList<IWuaDriverGateway.WuaDriver>)Drivers.AsReadOnly());

        public Task<(bool Success, string? Detail)> InstallDriverAsync(string hardwareId, System.Threading.CancellationToken cancellation = default)
        {
            InstallCalls++;
            return Task.FromResult<(bool, string?)>(InstallSucceeds ? (true, "WUA 驱动安装完成") : (false, "模拟失败"));
        }
    }

    private static DriverUpdateEntry WuaEntry(bool critical) => new(
        "HWID_GPU", "示例显卡", null, "27.21.14.5282",
        DriverUpdateKind.WuaDriver, critical, HashVerified: true);

    [Fact]
    public async Task 清单映射_WUA搜索结果_高危类由适配层判定_哈希按官方源已过()
    {
        var wua = new FakeWuaGateway
        {
            Drivers = { new("HWID_GPU", "示例显卡", "27.22", "Display adapters"), new("HWID_PRN", "打印机", "1.1", "Printer") },
        };
        var adapter = new WindowsDriverUpdateAdapter(wua, _ => (0, string.Empty, string.Empty));

        var list = await adapter.ListDriverUpdatesAsync();

        Assert.Equal(2, list.Count);
        Assert.True(list[0].IsCriticalDevice);  // 显卡
        Assert.False(list[1].IsCriticalDevice); // 打印机
        Assert.True(list[0].HashVerified);
        Assert.Equal(DriverUpdateKind.WuaDriver, list[0].Kind);
    }

    [Fact]
    public async Task 备份_pnputil零退出回真_非零回假_空目录拒绝()
    {
        var wua = new FakeWuaGateway();
        var seen = new List<string>();
        var adapter = new WindowsDriverUpdateAdapter(wua, a => { seen.Add(a); return (0, "导出成功", string.Empty); });

        Assert.True(await adapter.BackupDriverAsync("HWID", @"D:\TaoKeeper\DriverBackup"));
        Assert.Contains("export-driver", seen[0]);
        Assert.Contains("D:\\TaoKeeper\\DriverBackup", seen[0]);

        var failing = new WindowsDriverUpdateAdapter(new FakeWuaGateway(), _ => (5, string.Empty, "access denied"));
        Assert.False(await failing.BackupDriverAsync("HWID", @"D:\TaoKeeper\DriverBackup"));
        Assert.False(await adapter.BackupDriverAsync("HWID", string.Empty));
    }

    [Fact]
    public async Task 升级动作_WUA通道_成功失败均回填结果()
    {
        var okGateway = new FakeWuaGateway();
        var okAdapter = new WindowsDriverUpdateAdapter(okGateway, _ => (0, string.Empty, string.Empty));
        var ok = await okAdapter.ApplyDriverUpdateAsync(WuaEntry(critical: false));
        Assert.True(ok.Success);
        Assert.True(ok.BackupCompleted);
        Assert.Equal(1, okGateway.InstallCalls);

        var failGateway = new FakeWuaGateway { InstallSucceeds = false };
        var failAdapter = new WindowsDriverUpdateAdapter(failGateway, _ => (0, string.Empty, string.Empty));
        var fail = await failAdapter.ApplyDriverUpdateAsync(WuaEntry(critical: false));
        Assert.False(fail.Success);
    }

    [Fact]
    public async Task 厂商包不走WUA通道_返回明确指引()
    {
        var adapter = new WindowsDriverUpdateAdapter(new FakeWuaGateway(), _ => (0, string.Empty, string.Empty));
        var vendor = new DriverUpdateEntry("HWID", "厂商驱动", "1.0", "2.0", DriverUpdateKind.VendorPackagePnpUtil, false, true);
        var result = await adapter.ApplyDriverUpdateAsync(vendor);
        Assert.False(result.Success);
        Assert.Contains("pnputil", result.Detail);
    }

    [Fact]
    public void 危险inf路径拒绝_合法路径放行()
    {
        Assert.False(WindowsDriverUpdateAdapter.IsSafeInfPath("D:\\x\\a.inf; del /f"));
        Assert.False(WindowsDriverUpdateAdapter.IsSafeInfPath(string.Empty));
        Assert.True(WindowsDriverUpdateAdapter.IsSafeInfPath(@"D:\drivers\nvlt.inf"));
        Assert.True(WindowsDriverUpdateAdapter.IsSafeInfPath(@"\\server\share\nvlt.inf"));
    }
}
