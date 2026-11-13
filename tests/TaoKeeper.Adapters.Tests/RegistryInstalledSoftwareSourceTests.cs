using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Adapters;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Adapters.Tests;

/// <summary>注册表假读取：注入用例登记，不碰真注册表。</summary>
internal sealed class FakeUninstallRegistryReader(params RegistryUninstallRow[] rows) : IUninstallRegistryReader
{
    public IReadOnlyList<RegistryUninstallRow> ReadUninstallKeys() => rows;
}

/// <summary>
/// 已装软件注册表枚举纯逻辑测试（R-P2-1）：过滤、去重、大小展示、真实口径全对上。
/// 真注册表读取归 Windows 真机（待真机项），沙箱只测过滤与投影逻辑。
/// </summary>
public class RegistryInstalledSoftwareSourceTests
{
    private static RegistryUninstallRow Row(
        string name, string? version = "1.0", string? uninstall = "C:\\setup.exe /uninstall",
        long? sizeKb = 204800, bool system = false, string? rowKey = "HKLM|x") =>
        new(name, version, "某厂", uninstall, "C:\\app", sizeKb, system, rowKey);

    [Fact]
    public void 过滤_系统组件与空名条目不入清单()
    {
        var rows = new[]
        {
            Row("正常软件"),
            Row("系统组件", system: true),
            Row("", uninstall: null),
            new RegistryUninstallRow("  ", "1.0", null, "x", null, null, false, "HKCU|y"),
        };

        var list = RegistryInstalledSoftwareSource.Collect(new FakeUninstallRegistryReader(rows));

        var entry = Assert.Single(list);
        Assert.Equal("正常软件", entry.Name);
    }

    [Fact]
    public void 过滤_无卸载入口的系统补丁类登记不入清单()
    {
        // KB+数字 命名且无 UninstallString → Windows 更新类，用户不卸，过滤
        var update = new RegistryUninstallRow("KB5034123 更新", null, null, null, null, null, false, "HKLM|kb1");
        // 普通软件即便登记没带卸载串也保留（官方入口由卸载链另寻，不让软件凭空消失）
        var app = new RegistryUninstallRow("便携工具", "2.0", null, null, null, null, false, "HKLM|app1");

        var list = RegistryInstalledSoftwareSource.Collect(new FakeUninstallRegistryReader(update, app));

        var entry = Assert.Single(list);
        Assert.Equal("便携工具", entry.Name);
    }

    [Fact]
    public void 去重_同登记键双视图只留一条_无RowKey按名称版本指纹()
    {
        var dup = Row("微信", "3.9.9", rowKey: "HKLM|wechat");
        var dupAgain = Row("微信", "3.9.9", rowKey: "HKLM-WOW|wechat");
        var noKeyA = Row("旧登记甲", "1.0", rowKey: null);
        var noKeyB = Row("旧登记甲", "1.0", rowKey: null);
        var noKeyDiff = Row("旧登记甲", "1.1", rowKey: null);

        var list = RegistryInstalledSoftwareSource.Collect(new FakeUninstallRegistryReader(
            dup, dupAgain, noKeyA, noKeyB, noKeyDiff));

        Assert.Equal(3, list.Count); // 微信 1 + 旧登记甲 1（1.0/1.1 各算登记不同软件）
        Assert.Equal(2, list.Count(e => e.Name == "旧登记甲"));
    }

    [Fact]
    public void 大小展示_KB到MB到GB逐档_未知显占位()
    {
        Assert.Equal("—", RegistryInstalledSoftwareSource.SizeText(null));
        Assert.Equal("512 KB", RegistryInstalledSoftwareSource.SizeText(512));
        Assert.Equal("200 MB", RegistryInstalledSoftwareSource.SizeText(204800));
        Assert.Equal("1.5 MB", RegistryInstalledSoftwareSource.SizeText(1536));
        Assert.Equal("2.5 GB", RegistryInstalledSoftwareSource.SizeText(2 * 1024 * 1024 + 512 * 1024));
    }

    [Fact]
    public void 条目投影_卸载串作Id_版本缺省如实保空()
    {
        var row = new RegistryUninstallRow("七压缩", null, null, "C:\\7z\\un.exe", null, 1024, false, "HKLM|7z");

        var list = RegistryInstalledSoftwareSource.Collect(new FakeUninstallRegistryReader(row));

        var entry = Assert.Single(list);
        Assert.Equal("C:\\7z\\un.exe", entry.Id);
        Assert.Null(entry.InstalledVersion);
        Assert.Equal(SoftwareSourceKind.OfficialPageManual, entry.Source);
        Assert.Equal("1 MB", RegistryInstalledSoftwareSource.SizeText(1024));
    }
}
