using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>体检上下文汇总器测试：各模块真实产出 → HealthContext → 评分链路打通。</summary>
public class CheckupContextBuilderTests
{
    private static JunkScanResult Junk(long totalBytes) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Array.Empty<JunkCandidate>(), totalBytes);

    private static StartupItemEntry Item(bool enabled) =>
        new($"item-{Guid.NewGuid():N}", "测试启动项", StartupItemSource.RegistryRun,
            "测试厂商", enabled, StartupRiskLevel.Low, Command: "C:\\test.exe");

    private static DuplicateGroup Group(long sizeBytes, int fileCount)
    {
        var files = Enumerable.Range(0, fileCount)
            .Select(i => new FileEntry($"C:\\dup\\{i}.bin", $"{i}.bin", "C:\\dup", sizeBytes,
                DateTimeOffset.UtcNow, FileAttributesSlim.None))
            .ToList();
        return new DuplicateGroup("sha", sizeBytes, files);
    }

    [Fact]
    public void 全部未测_各维度保持Null_不冒充实测()
    {
        var context = CheckupContextBuilder.Build();

        Assert.Null(context.JunkReclaimableBytes);
        Assert.Null(context.DuplicateReclaimableBytes);
        Assert.Null(context.StartupItemCount);
        Assert.Null(context.SystemDiskFreePercent);
        Assert.Null(context.VirtualMemoryHint);
        Assert.Null(context.StorageSenseEnabled);
        Assert.Equal(HealthContext.Empty, context);
    }

    [Fact]
    public void 垃圾与重复汇总_取TotalBytes与分组可释放量求和()
    {
        var context = CheckupContextBuilder.Build(
            junkScan: Junk(6L * 1024 * 1024 * 1024),
            duplicateGroups: new List<DuplicateGroup> { Group(100, 2), Group(200, 3) });

        Assert.Equal(6L * 1024 * 1024 * 1024, context.JunkReclaimableBytes);
        // 100×(2-1) + 200×(3-1) = 500
        Assert.Equal(500, context.DuplicateReclaimableBytes);
    }

    [Fact]
    public void 启动项计数_只计启用态()
    {
        var context = CheckupContextBuilder.Build(startupItems: new List<StartupItemEntry>
        {
            Item(enabled: true), Item(enabled: true), Item(enabled: false),
        });

        Assert.Equal(2, context.StartupItemCount);
    }

    [Fact]
    public void 系统盘余量_按剩余与总量换算百分比()
    {
        var context = CheckupContextBuilder.Build(
            systemDiskFreeBytes: 15L * 1024 * 1024 * 1024,
            systemDiskTotalBytes: 100L * 1024 * 1024 * 1024);

        Assert.Equal(15.0, context.SystemDiskFreePercent!.Value, precision: 9);
    }

    [Fact]
    public void 系统盘字节数防呆_非法输入报红不出假百分比()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CheckupContextBuilder.Build(
            systemDiskFreeBytes: 100, systemDiskTotalBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CheckupContextBuilder.Build(
            systemDiskFreeBytes: -1, systemDiskTotalBytes: 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => CheckupContextBuilder.Build(
            systemDiskFreeBytes: 200, systemDiskTotalBytes: 100));
        // 只给总量不给余量：不换算（未测语义保持）
        Assert.Null(CheckupContextBuilder.Build(systemDiskTotalBytes: 100).SystemDiskFreePercent);
    }

    [Fact]
    public void 存储感知_取方案开关_未提供保持未测()
    {
        var off = CheckupContextBuilder.Build(storageSensePlan: StorageSensePlan.Default);
        Assert.False(off.StorageSenseEnabled!.Value);

        var on = CheckupContextBuilder.Build(storageSensePlan:
            new StorageSensePlan(true, StorageCleanupScope.None, 7, null, true));
        Assert.True(on.StorageSenseEnabled!.Value);

        Assert.Null(CheckupContextBuilder.Build().StorageSenseEnabled);
    }

    [Fact]
    public void 虚拟内存_完全关闭给固定提示_未提供保持未测()
    {
        var disabled = CheckupContextBuilder.Build(pageFileSettings:
            new PageFileSettings(PageFilePreset.SystemManaged, null, null, null, FullyDisabled: true));
        Assert.Contains("完全关闭", disabled.VirtualMemoryHint);
        Assert.Contains("转储", disabled.VirtualMemoryHint);

        Assert.Null(CheckupContextBuilder.Build().VirtualMemoryHint);
        Assert.Null(CheckupContextBuilder.Build(
            pageFileSettings: new PageFileSettings(PageFilePreset.SmartRecommendation, null, null, null)).VirtualMemoryHint);
    }

    [Fact]
    public void 虚拟内存_自定义参数按物理内存核对_物理内存缺失跳过核对()
    {
        // 合法自定义参数：初始/最大在合理区间，无提示
        var valid = CheckupContextBuilder.Build(
            pageFileSettings: new PageFileSettings(PageFilePreset.Custom, "D", 8192, 16384),
            physicalMemoryBytes: 8L * 1024 * 1024 * 1024);
        Assert.Null(valid.VirtualMemoryHint);

        // 最大值超出上限（4 倍物理内存）：核对给出提示
        var over = CheckupContextBuilder.Build(
            pageFileSettings: new PageFileSettings(PageFilePreset.Custom, "D", 8192, 1024 * 1024),
            physicalMemoryBytes: 8L * 1024 * 1024 * 1024);
        Assert.Contains("超出合理区间", over.VirtualMemoryHint);

        // 物理内存未知：跳过核对，不按 0 字节假报错
        var unknown = CheckupContextBuilder.Build(
            pageFileSettings: new PageFileSettings(PageFilePreset.Custom, "D", 8192, 1024 * 1024));
        Assert.Null(unknown.VirtualMemoryHint);
    }

    [Fact]
    public void 端到端_汇总上下文进引擎_评分与发现按规则产出()
    {
        var context = CheckupContextBuilder.Build(
            junkScan: Junk(6L * 1024 * 1024 * 1024),                       // Warning 15 分
            duplicateGroups: new List<DuplicateGroup> { Group(100, 2), Group(200, 3) }, // 500B，低于 2GB 阈值不报
            startupItems: new List<StartupItemEntry> { Item(true), Item(false) },       // 1 项，不报
            systemDiskFreeBytes: 9L * 1024 * 1024 * 1024,
            systemDiskTotalBytes: 100L * 1024 * 1024 * 1024,               // 9% → Critical 30 分
            storageSensePlan: StorageSensePlan.Default);                    // Info 0 分

        var result = new HealthCheckEngine().Run(context);

        Assert.Equal(100 - 15 - 30, result.Score);
        var ids = result.Findings.Select(f => f.RuleId).ToList();
        Assert.Contains("junk-accumulation", ids);
        Assert.Contains("system-disk-space", ids);
        Assert.Contains("storage-sense", ids);
        Assert.DoesNotContain("duplicate-cluster", ids); // 500B 未达阈值
        Assert.DoesNotContain("startup-items", ids);     // 1 项未达阈值
    }
}
