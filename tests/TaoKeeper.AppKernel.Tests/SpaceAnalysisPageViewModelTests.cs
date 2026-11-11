using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

public class SpaceAnalysisPageViewModelTests
{
    private static MftEntryInfo E(ulong recordNo, ulong parent, string name, bool isDir = false) =>
        new(recordNo, parent, name, 0, 0, isDir, default, default, default);

    private static MftDirAggregate A(ulong recordNo, long size, long files) =>
        new(recordNo, unchecked((ulong)size), unchecked((ulong)files));

    /// <summary>构造与流式扫描同形的合成结果（条目树：目录六(6←5)、目录七(7←6)；聚合体积可配）。</summary>
    private static MftStreamScanResult Result(
        IReadOnlyList<MftDirAggregate> aggs,
        long skipped = 1, long bytesRead = 12 * 1024,
        ulong orphanSize = 0, ulong orphanFiles = 0) =>
        new(
            new List<MftEntryInfo>
            {
                E(6, 5, "目录六", isDir: true),
                E(7, 6, "目录七", isDir: true),
                E(8, 7, "文件八.txt"),
                E(9, 6, "文件九.txt"),
            },
            aggs, bytesRead, skipped, orphanSize, orphanFiles);

    [Fact]
    public void 空间分析_目录排序路径重建与汇总()
    {
        var vm = new SpaceAnalysisPageViewModel().BuildFromStream(Result(new[]
        {
            A(7, 100, 1), A(6, 300, 2), A(9, 50, 1),
        }), topN: 2);

        // Top-N 体积降序，同体积按记录号升序
        Assert.Equal(2, vm.TopDirectories.Count);
        Assert.Equal(6ul, vm.TopDirectories[0].RecordNo);
        Assert.Equal("目录六", vm.TopDirectories[0].PathText);     // 根 5 不在映射，父链到根即止
        Assert.False(vm.TopDirectories[0].PathFallback);
        Assert.Equal("300 B", vm.TopDirectories[0].SizeText);
        Assert.Equal(2, vm.TopDirectories[0].FileCount);
        Assert.Equal(7ul, vm.TopDirectories[1].RecordNo);
        Assert.Equal("目录六\\目录七", vm.TopDirectories[1].PathText);

        Assert.Equal(1, vm.HiddenDirectoryCount);                  // 记录 9 未进 Top-2
        Assert.Equal(0ul, vm.OrphanSize);
        Assert.Equal("目录 3 项，记录 4 条，坏记录 1 条，已读 12 KB", vm.SummaryText);
        Assert.True(vm.HasItems);
    }

    [Fact]
    public void 空间分析_路径失败退裸名并如实标记()
    {
        // 聚合含父链断裂的目录（10←99，条目里父不在）：MftPathBuilder 拒链，退裸名
        var vm = new SpaceAnalysisPageViewModel().BuildFromStream(Result(new[]
        {
            A(10, 4096, 3),
        }));

        Assert.Single(vm.TopDirectories);
        var item = vm.TopDirectories[0];
        Assert.Equal(10ul, item.RecordNo);
        Assert.True(item.PathFallback);
        Assert.Equal("记录 10", item.PathText); // 条目里无该记录名，按「记录 N」转述不编路径
    }

    [Fact]
    public void 空间分析_孤儿入汇总不入清单()
    {
        var vm = new SpaceAnalysisPageViewModel().BuildFromStream(Result(
            new[] { A(6, 100, 1) },
            orphanSize: 777 + 888, orphanFiles: 2));

        Assert.Single(vm.TopDirectories);
        Assert.Equal(1665ul, vm.OrphanSize);
        Assert.Equal(2ul, vm.OrphanFiles);
        Assert.DoesNotContain(vm.TopDirectories, i => i.RecordNo == 0ul);
        Assert.Contains("未归档 2 KB（2 项）", vm.SummaryText); // 1665B 按 KB 转述四舍五入为 2 KB
    }

    [Fact]
    public void 空间分析_空卷与参数防护()
    {
        var vm = new SpaceAnalysisPageViewModel().BuildFromStream(Result(Array.Empty<MftDirAggregate>()));
        Assert.False(vm.HasItems);
        Assert.Empty(vm.TopDirectories);
        Assert.Equal(0, vm.HiddenDirectoryCount);
        Assert.Equal("目录 0 项，记录 4 条，坏记录 1 条，已读 12 KB", vm.SummaryText);

        Assert.Throws<ArgumentNullException>(
            () => new SpaceAnalysisPageViewModel().BuildFromStream(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SpaceAnalysisPageViewModel().BuildFromStream(Result(Array.Empty<MftDirAggregate>()), topN: 0));
    }

    [Fact]
    public void 空间分析_体积分层色随主题取值()
    {
        var vm = new SpaceAnalysisPageViewModel();
        Assert.Equal(ThemeTokens.Shuimo.Cinnabar, vm.TierColorOf(2L * 1024 * 1024 * 1024, ThemeTokens.Shuimo));  // ≥1GB 朱砂
        Assert.Equal(ThemeTokens.Shuimo.Gold, vm.TierColorOf(200L * 1024 * 1024, ThemeTokens.Shuimo));           // ≥100MB 金
        Assert.Equal(ThemeTokens.Shuimo.Bamboo, vm.TierColorOf(99L * 1024 * 1024, ThemeTokens.Shuimo));          // 其余竹青
        // 三主题同语义不同值（即时取值，不缓存色值）
        Assert.Equal(ThemeTokens.Mobai.Cinnabar, vm.TierColorOf(2L * 1024 * 1024 * 1024, ThemeTokens.Mobai));
        Assert.NotEqual(ThemeTokens.Shuimo.Cinnabar, ThemeTokens.Mobai.Cinnabar);
    }

    private static ScanResult PortableScan(IReadOnlyList<DirectoryStats> dirs, long totalBytes, int fileCount) =>
        new(ScanMode.Quick, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Array.Empty<FileEntry>(), dirs, Array.Empty<FileEntry>(), totalBytes, fileCount);

    [Fact]
    public void 空间分析_便携降级模式排序汇总与模式标记()
    {
        var vm = new SpaceAnalysisPageViewModel().BuildFromPortableScan(PortableScan(new[]
        {
            new DirectoryStats("C:\\Users\\甲", 100, 2),
            new DirectoryStats("C:\\Users\\乙", 500, 5),
            new DirectoryStats("C:\\Windows", 300, 9),
        }, totalBytes: 900, fileCount: 16), topN: 2);

        Assert.True(vm.PortableMode);
        Assert.Null(vm.LastScan);
        Assert.Equal(2, vm.TopDirectories.Count);
        Assert.Equal("C:\\Users\\乙", vm.TopDirectories[0].PathText); // 500 最大
        Assert.Equal("C:\\Windows", vm.TopDirectories[1].PathText);   // 300 次之
        Assert.Equal(0ul, vm.TopDirectories[0].RecordNo);             // 便携模式无记录号语义
        Assert.False(vm.TopDirectories[0].PathFallback);
        Assert.Equal(1, vm.HiddenDirectoryCount);
        Assert.Equal(0ul, vm.OrphanSize);                             // 便携模式无孤儿语义
        Assert.Equal("便携模式：目录 3 项，文件 16 个，合计 900 B", vm.SummaryText);
        Assert.True(vm.HasItems);
    }

    [Fact]
    public void 空间分析_两种构建互切与防护()
    {
        var vm = new SpaceAnalysisPageViewModel();
        Assert.False(vm.PortableMode); // 初始：尚未扫描非便携

        // 便携 → 流式互切，模式标记跟随最后一次构建
        vm.BuildFromPortableScan(PortableScan(Array.Empty<DirectoryStats>(), 0, 0));
        Assert.True(vm.PortableMode);
        Assert.False(vm.HasItems);
        Assert.Equal("便携模式：目录 0 项，文件 0 个，合计 0 B", vm.SummaryText);

        vm.BuildFromStream(Result(new[] { A(6, 100, 1) }));
        Assert.False(vm.PortableMode); // 流式构建复位便携标记
        Assert.NotNull(vm.LastScan);

        vm.BuildFromPortableScan(PortableScan(Array.Empty<DirectoryStats>(), 0, 0));
        Assert.True(vm.PortableMode);

        // 防护：空扫描与非法 Top-N
        Assert.Throws<ArgumentNullException>(() => vm.BuildFromPortableScan(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.BuildFromPortableScan(PortableScan(Array.Empty<DirectoryStats>(), 0, 0), topN: 0));
    }
}
