using System;
using System.Linq;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>页面文件方案编排（纯逻辑层）：档位映射、目标条目编排、现值摘要与相等判定。</summary>
public class PageFilePlanComposerTests
{
    private const long SixteenGb = 16L * 1024 * 1024 * 1024;

    [Fact]
    public void FromPlanKey_MapsNamedPlans()
    {
        Assert.Equal(PageFilePreset.SmartRecommendation, PageFilePlanComposer.FromPlanKey("smart")!.Preset);
        Assert.Equal(PageFilePreset.PerformanceFirst, PageFilePlanComposer.FromPlanKey("perf")!.Preset);
        Assert.Equal(PageFilePreset.SpaceFirst, PageFilePlanComposer.FromPlanKey("space")!.Preset);
        Assert.Equal(PageFilePreset.SystemManaged, PageFilePlanComposer.FromPlanKey("system")!.Preset);
    }

    [Fact]
    public void FromPlanKey_CustomAndUnknown_ReturnNullForHonestReceipt()
    {
        Assert.Null(PageFilePlanComposer.FromPlanKey("custom"));
        Assert.Null(PageFilePlanComposer.FromPlanKey("whatever"));
    }

    [Fact]
    public void ComposeTargetEntries_SystemManaged_IsAutomaticEntry()
    {
        var settings = PageFilePlanComposer.FromPlanKey("system")!;
        var entries = PageFilePlanComposer.ComposeTargetEntries(settings, SixteenGb, "C:");
        Assert.Single(entries);
        Assert.Equal(PageFilePlanComposer.SystemManagedEntry, entries[0]);
    }

    [Fact]
    public void ComposeTargetEntries_PerformanceFirst_UsesCoreSuggestedValues()
    {
        var settings = PageFilePlanComposer.FromPlanKey("perf")!;
        var entries = PageFilePlanComposer.ComposeTargetEntries(settings, SixteenGb, "C:");
        // 16GB 物理 → ClampMb=16384 → perf: 初始 32768 / 最大 65536
        Assert.Equal(@"C:\pagefile.sys 32768 65536", entries[0]);
    }

    [Fact]
    public void ComposeTargetEntries_SpaceFirst_IsFixedSmall()
    {
        var entries = PageFilePlanComposer.ComposeTargetEntries(
            PageFilePlanComposer.FromPlanKey("space")!, SixteenGb, "D:");
        Assert.Equal(@"D:\pagefile.sys 2048 4096", entries[0]);
    }

    [Fact]
    public void ComposeTargetEntries_RootWithBackslash_NormalizedToBareDrive()
    {
        // r39 实锤回归：Path.GetPathRoot 返回 "C:\"，盘符必须归一为裸字母，不能写出 C:\:\pagefile.sys
        var entries = PageFilePlanComposer.ComposeTargetEntries(
            PageFilePlanComposer.FromPlanKey("perf")!, SixteenGb, @"C:\");
        Assert.Equal(@"C:\pagefile.sys 32768 65536", entries[0]);
    }

    [Fact]
    public void ComposeTargetEntries_NoSuggestedValues_ThrowsInsteadOfFakeNumbers()
    {
        var settings = new PageFileSettings(PageFilePreset.Custom, null, null, null);
        Assert.Throws<ArgumentException>(() =>
            PageFilePlanComposer.ComposeTargetEntries(settings, SixteenGb, "C:"));
    }

    [Fact]
    public void Describe_ClassifiesCurrentEntries()
    {
        Assert.Equal("（未配置）", PageFilePlanComposer.Describe(Array.Empty<string?>()));
        Assert.Contains("系统托管", PageFilePlanComposer.Describe(new[] { @"?:\pagefile.sys" }));
        Assert.Contains(@"C:\pagefile.sys", PageFilePlanComposer.Describe(new[] { @"C:\pagefile.sys 2048 4096" }));
    }

    [Fact]
    public void SameAsCurrent_IgnoresWhitespaceAndCase()
    {
        Assert.True(PageFilePlanComposer.SameAsCurrent(
            new[] { @"C:\pagefile.sys 2048 4096" }, new[] { @" c:\pagefile.sys  2048 4096 " }));
        Assert.False(PageFilePlanComposer.SameAsCurrent(
            new[] { PageFilePlanComposer.SystemManagedEntry }, new[] { @"C:\pagefile.sys 2048 4096" }));
        Assert.False(PageFilePlanComposer.SameAsCurrent(new[] { "a" }, Array.Empty<string?>()));
    }
}
