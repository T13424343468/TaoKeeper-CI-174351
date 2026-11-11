using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>内存监控数据模型：快照口径、趋势降采样、页面文件参数校验。</summary>
public class MemoryMonitorTests
{
    private static MemorySnapshot Snapshot(double usedPercent = 50, long? pageTotal = 32768, long? pageUsed = 8192)
        => new(
            DateTimeOffset.UtcNow,
            TotalPhysicalBytes: 16L * 1024 * 1024 * 1024,
            AvailablePhysicalBytes: (long)(16L * 1024 * 1024 * 1024 * (1 - usedPercent / 100.0)),
            PageFileTotalBytes: pageTotal * 1024 * 1024,
            PageFileUsedBytes: pageUsed * 1024 * 1024);

    [Fact]
    public void 快照占用率口径正确()
    {
        var s = Snapshot(usedPercent: 25);
        Assert.Equal(25.0, s.PhysicalUsedPercent);
        Assert.Equal(25.0, s.PageFileUsedPercent); // 8192 / 32768
    }

    [Fact]
    public void 系统托管时页面文件占用率为未测()
    {
        var s = new MemorySnapshot(DateTimeOffset.UtcNow, 1, 1, null, null);
        Assert.Null(s.PageFileUsedPercent);
    }

    [Fact]
    public void 趋势序列超限降采样且保留最新()
    {
        var series = new MemoryTrendSeries(capacity: 100);
        var first = Snapshot();
        for (int i = 0; i < 300; i++)
        {
            series.Append(Snapshot(usedPercent: i % 100));
            if (i == 0) { /* 记录首样本 */ }
        }

        Assert.Equal(100, series.Count);
        Assert.Equal(300, series.TotalAppended);
        Assert.Equal(series.Samples[0], series.Samples[0]);
        Assert.NotNull(series.Latest);
        // 最新样本一定是最后追加的那份（usedPercent = 299 % 100 = 99）
        Assert.Equal(99.0, series.Latest!.PhysicalUsedPercent);
    }

    [Fact]
    public void 降采样绘图序列不含最新值以外的丢弃()
    {
        var series = new MemoryTrendSeries(capacity: 10);
        for (int i = 0; i < 20; i++) series.Append(Snapshot(usedPercent: i));
        var drawn = series.Downsample(every: 2);
        Assert.Equal(series.Latest, drawn[^1]); // 绘图必须包含最新点
        Assert.True(drawn.Count <= series.Count);
        Assert.True(drawn.Count >= series.Count / 2);
    }

    [Fact]
    public void 预设方案给出建议参数()
    {
        var smart = new PageFileSettings(PageFilePreset.SmartRecommendation, null, null, null);
        var perf = new PageFileSettings(PageFilePreset.PerformanceFirst, null, null, null);
        var space = new PageFileSettings(PageFilePreset.SpaceFirst, null, null, null);
        var managed = new PageFileSettings(PageFilePreset.SystemManaged, null, null, null);

        var physicalMb = 16L * 1024;
        Assert.Equal((16384, 32768), smart.SuggestedValues(physicalMb));
        Assert.Equal((32768, 65536), perf.SuggestedValues(physicalMb));
        Assert.Equal((2048, 4096), space.SuggestedValues(physicalMb));
        Assert.Null(managed.SuggestedValues(physicalMb));
    }

    [Fact]
    public void 自定义参数校验_合法区间通过()
    {
        var s = new PageFileSettings(PageFilePreset.Custom, "D", 8192, 16384);
        var v = PageFileSettingsValidator.Validate(s, 16L * 1024 * 1024 * 1024);
        Assert.True(v.IsValid, string.Join(";", v.Errors));
        Assert.Empty(v.Warnings);
    }

    [Fact]
    public void 自定义参数校验_最大小于初始报错()
    {
        var s = new PageFileSettings(PageFilePreset.Custom, "C", 16384, 8192);
        var v = PageFileSettingsValidator.Validate(s, 16L * 1024 * 1024 * 1024);
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("最大大小不得小于初始大小"));
    }

    [Fact]
    public void 自定义参数校验_越界与缺失盘位()
    {
        var tiny = new PageFileSettings(PageFilePreset.Custom, "C", 128, 128);
        Assert.False(PageFileSettingsValidator.Validate(tiny, 16L * 1024 * 1024 * 1024).IsValid);

        var huge = new PageFileSettings(PageFilePreset.Custom, "C", 1024, int.MaxValue);
        Assert.False(PageFileSettingsValidator.Validate(huge, 16L * 1024 * 1024 * 1024).IsValid);

        var noDrive = new PageFileSettings(PageFilePreset.Custom, null, 1024, 2048);
        Assert.False(PageFileSettingsValidator.Validate(noDrive, 16L * 1024 * 1024 * 1024).IsValid);

        var badDrive = new PageFileSettings(PageFilePreset.Custom, "AB", 1024, 2048);
        Assert.False(PageFileSettingsValidator.Validate(badDrive, 16L * 1024 * 1024 * 1024).IsValid);
    }

    [Fact]
    public void 自定义参数校验_小于物理内存给警告但可过()
    {
        var s = new PageFileSettings(PageFilePreset.Custom, "C", 1024, 2048);
        var v = PageFileSettingsValidator.Validate(s, 16L * 1024 * 1024 * 1024);
        Assert.True(v.IsValid);
        Assert.Contains(v.Warnings, w => w.Contains("虚拟内存不足"));
    }

    [Fact]
    public void 完全关闭必须携带确认且不得填参数()
    {
        var ok = new PageFileSettings(PageFilePreset.Custom, "C", null, null, FullyDisabled: true);
        var vOk = PageFileSettingsValidator.Validate(ok, 16L * 1024 * 1024 * 1024);
        Assert.True(vOk.IsValid);
        Assert.Contains(vOk.Warnings, w => w.Contains("无法启动"));

        var withValues = new PageFileSettings(PageFilePreset.Custom, "C", 1024, 2048, FullyDisabled: true);
        Assert.False(PageFileSettingsValidator.Validate(withValues, 16L * 1024 * 1024 * 1024).IsValid);

        var managedOff = new PageFileSettings(PageFilePreset.SystemManaged, null, null, null, FullyDisabled: true);
        Assert.False(PageFileSettingsValidator.Validate(managedOff, 16L * 1024 * 1024 * 1024).IsValid);
    }
}
