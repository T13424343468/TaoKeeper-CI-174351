using System;
using System.Linq;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>内存管理页视图模型测试：压力档位联动告警线、趋势降采样、占用排行截取、页面文件建议与校验转述。</summary>
public class MemPageViewModelTests
{
    private static MemorySnapshot Snap(double usedPercent, long total = 16L * 1024 * 1024 * 1024) =>
        new(DateTimeOffset.UtcNow, total,
            (long)(total * (100 - usedPercent) / 100),
            PageFileTotalBytes: 8L * 1024 * 1024 * 1024,
            PageFileUsedBytes: (long)(8L * 1024 * 1024 * 1024 * 0.4));

    [Fact]
    public void 压力档位随告警线联动()
    {
        var vm = new MemPageViewModel(alertPercent: 85);
        vm.ApplySnapshot(Snap(84.9));
        Assert.Equal(MemoryPressureLevel.Normal, vm.PressureLevel);

        vm.ApplySnapshot(Snap(85));
        Assert.Equal(MemoryPressureLevel.Alert, vm.PressureLevel);   // 达到告警线

        vm.ApplySnapshot(Snap(95));
        Assert.Equal(MemoryPressureLevel.Critical, vm.PressureLevel);
    }

    [Fact]
    public void 告警线为0时_用户已关提醒_档位恒常()
    {
        var vm = new MemPageViewModel(alertPercent: 0);
        vm.ApplySnapshot(Snap(99));
        Assert.Equal(MemoryPressureLevel.Normal, vm.PressureLevel);  // 不打扰用户
        Assert.Equal("99%", vm.UsageText);                           // 数值照常展示
    }

    [Fact]
    public void 展示文本与趋势序列正确()
    {
        var vm = new MemPageViewModel();
        vm.ApplySnapshot(Snap(60));
        vm.ApplySnapshot(Snap(62));
        Assert.Equal("62%", vm.UsageText);
        Assert.Contains("物理内存", vm.DetailText);
        Assert.Contains("页面文件", vm.DetailText);
        Assert.Equal(2, vm.Trend.Count);                             // 环形序列随快照增长
    }

    [Fact]
    public void 占用排行_按工作集降序截取TopN()
    {
        var vm = new MemPageViewModel { TopCount = 2 };
        vm.ApplyTopProcesses(new[]
        {
            new MemoryTopEntry(101, "甲", 100, true),
            new MemoryTopEntry(102, "乙", 300, false),
            new MemoryTopEntry(103, "丙", 200, null),
        });
        Assert.Equal(new[] { "乙", "丙" }, vm.TopProcesses.Select(p => p.ProcessName));
        Assert.Equal(2, vm.TopProcesses.Count);                      // TopN 截断
    }

    [Fact]
    public void 页面文件建议与校验_转述口径一致()
    {
        var settings = new PageFileSettings(PageFilePreset.SmartRecommendation, null, null, null);
        var suggest = MemPageViewModel.SuggestPageFile(settings, 16L * 1024 * 1024 * 1024);
        Assert.NotNull(suggest);
        Assert.Equal(16384, suggest!.Value.InitialMb);               // 16GB 物理内存 → 建议初始 16GB

        var bad = new PageFileSettings(PageFilePreset.Custom, "C", 128, 256); // 低于下限
        var result = MemPageViewModel.ValidatePageFile(bad, 16L * 1024 * 1024 * 1024);
        Assert.False(result.IsValid);                                // 校验错误原样转述
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void 压力色随三主题即时取值()
    {
        var vm = new MemPageViewModel();
        foreach (var theme in new[] { ThemeName.Shuimo, ThemeName.Tianqing, ThemeName.Mobai })
        {
            var tokens = ThemeTokens.Of(theme);
            Assert.Equal(tokens.Cinnabar, vm.PressureColorOf(MemoryPressureLevel.Critical, tokens));
            Assert.Equal(tokens.Gold, vm.PressureColorOf(MemoryPressureLevel.Alert, tokens));
            Assert.Equal(tokens.Bamboo, vm.PressureColorOf(MemoryPressureLevel.Normal, tokens));
        }
    }
}
