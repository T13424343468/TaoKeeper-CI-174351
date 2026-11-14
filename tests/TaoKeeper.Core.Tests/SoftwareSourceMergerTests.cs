using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// W7 软件管家双源合并测试：按名称+发布者归并、HashVerified true&gt;null&gt;false 择优、
/// 字段空缺由另一侧补齐、用户侧来源说明一律置 null（G7 零外显）、审计留原值可回溯。
/// </summary>
public class SoftwareSourceMergerTests
{
    private static SoftwareEntry Winget(string name, string? pub = "Contoso", string? version = "1.0",
        string? avail = null, bool? hash = null)
        => new($"WINGET\\{name}", name, pub, version, avail, SoftwareSourceKind.WingetCommunity, hash, "winget 社区源");

    private static SoftwareEntry Reg(string name, string? pub = "Contoso", string? version = "1.0")
        => new(@"C:\Uninstall\x.exe", name, pub, version, null, SoftwareSourceKind.OfficialPageManual, null, "注册表登记");

    [Fact]
    public void Merge_SameNameAndPublisher_UnifiesToOne()
    {
        var (entries, audit) = SoftwareSourceMerger.Merge(
            new List<SoftwareEntry> { Winget("示例浏览器") },
            new List<SoftwareEntry> { Reg("示例浏览器") });
        Assert.Single(entries);
        Assert.Equal("双源同软件，字段择优", Assert.Single(audit).Reason);
    }

    [Fact]
    public void Merge_DifferentPublisher_SeparateEntries()
    {
        var (entries, _) = SoftwareSourceMerger.Merge(
            new List<SoftwareEntry> { Winget("示例播放器", pub: "甲公司") },
            new List<SoftwareEntry> { Reg("示例播放器", pub: "乙公司") });
        Assert.Equal(2, entries.Count); // 同名不同发布者不合并（防错配）
    }

    [Fact]
    public void Merge_HashVerifiedTrue_WinsOverNull()
    {
        var (entries, _) = SoftwareSourceMerger.Merge(
            new List<SoftwareEntry> { Winget("示例编辑器", version: "2.0", hash: true) },
            new List<SoftwareEntry> { Reg("示例编辑器", version: "1.0") });
        var entry = Assert.Single(entries);
        Assert.Equal("2.0", entry.InstalledVersion); // 择优侧字段保留
        Assert.True(entry.HashVerified);
    }

    [Fact]
    public void Merge_HashNull_WinsOverFalse()
    {
        var (entries, _) = SoftwareSourceMerger.Merge(
            new List<SoftwareEntry> { Reg("示例工具", version: "1.0") },
            new List<SoftwareEntry> { Winget("示例工具", version: "1.0", hash: false) });
        var entry = Assert.Single(entries);
        Assert.Null(entry.HashVerified); // 未核验优于失配（失配不等于定论，但不当胜者）
    }

    [Fact]
    public void Merge_FillsMissingFieldsFromOtherSide()
    {
        // winget 有可升级版本、注册表有卸载入口（Id=UninstallString 保留在注册表侧胜者或补齐）
        var (entries, _) = SoftwareSourceMerger.Merge(
            new List<SoftwareEntry> { Winget("示例套件", version: "1.0", avail: "2.0") },
            new List<SoftwareEntry> { Reg("示例套件", version: null) });
        var entry = Assert.Single(entries);
        Assert.Equal("1.0", entry.InstalledVersion);
        Assert.Equal("2.0", entry.AvailableVersion); // 空缺由另一侧补齐
    }

    [Fact]
    public void Merge_UserSideSourceDisplay_AlwaysNull()
    {
        var (entries, _) = SoftwareSourceMerger.Merge(
            new List<SoftwareEntry> { Winget("软件甲"), Winget("软件乙", hash: true) },
            new List<SoftwareEntry> { Reg("软件甲"), Reg("软件丙") });
        Assert.All(entries, static e => Assert.Null(e.SourceDisplay)); // G7 来源零外显
        Assert.Equal(3, entries.Count); // 甲/乙/丙
    }

    [Fact]
    public void Merge_AuditKeepsOriginalValues()
    {
        var (entries, audit) = SoftwareSourceMerger.Merge(
            new List<SoftwareEntry> { Winget("审计样本", hash: true) },
            new List<SoftwareEntry> { Reg("审计样本") });
        var row = Assert.Single(audit);
        Assert.Equal("winget 社区源", row.WingetSourceDisplay); // 审计留原值
        Assert.Equal("注册表登记", row.RegistrySourceDisplay);
        Assert.Equal("审计样本", row.Name);
        Assert.Single(entries); // 审计与用户侧清单一一对应（每组一条）
    }

    [Fact]
    public void Merge_SingleSourceOnly_PassesThroughWithReason()
    {
        var (onlyRegistry, audit1) = SoftwareSourceMerger.Merge(
            null,
            new List<SoftwareEntry> { Reg("仅注册表") });
        Assert.Single(onlyRegistry);
        Assert.Equal("仅注册表登记", Assert.Single(audit1).Reason);
        Assert.Null(onlyRegistry[0].SourceDisplay);

        var (onlyWinget, audit2) = SoftwareSourceMerger.Merge(
            new List<SoftwareEntry> { Winget("仅 winget") },
            null);
        Assert.Single(onlyWinget);
        Assert.Equal("仅 winget 社区源", Assert.Single(audit2).Reason);
    }

    [Fact]
    public void Merge_NameWhitespaceAndCase_Normalized()
    {
        var (entries, _) = SoftwareSourceMerger.Merge(
            new List<SoftwareEntry> { Winget("Sample App", pub: "contoso") },
            new List<SoftwareEntry> { Reg("sampleapp", pub: "Contoso") });
        Assert.Single(entries); // 空白与大小写差异归一
    }
}
