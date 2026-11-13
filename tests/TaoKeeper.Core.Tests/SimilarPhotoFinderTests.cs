using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Services;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// 相似照片识别测试（R-P10-2a）：均值哈希确定性、海明距离、阈值连通分组（连拍成串）、
/// 不可解文件如实跳过、进度与取消语义。零写入铁律：全链只读。
/// </summary>
public class SimilarPhotoFinderTests
{
    /// <summary>构造文件条目（'/' 合成路径，不碰真实盘）。</summary>
    private static FileEntry Entry(string path, long size = 1024) =>
        new(path, "f", "/", size, DateTimeOffset.UtcNow, FileAttributesSlim.None);

    /// <summary>假灰度格来源：按路径查表给格；未登记=返回 null（模拟不可解）。</summary>
    private sealed class FakeGrids : IImageGridSource
    {
        private readonly Dictionary<string, (int W, int H, byte[] Gray)> _table;
        public int DecodeCalls { get; private set; }

        public FakeGrids(params (string Path, int W, int H, byte[] Gray)[] entries) =>
            _table = entries.ToDictionary(e => e.Path, e => (e.W, e.H, e.Gray),
                StringComparer.OrdinalIgnoreCase);

        public Task<ImageGrayGrid?> DecodeAsync(string fullPath, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            DecodeCalls++;
            return Task.FromResult(_table.TryGetValue(fullPath, out var g)
                ? new ImageGrayGrid(g.W, g.H, g.Gray)
                : null);
        }
    }

    /// <summary>均匀灰度格：全 64 格同值 → 均值哈希全 1（都等于均值口径）。</summary>
    private static byte[] FlatGray(byte value) => Enumerable.Repeat(value, 64).ToArray();

    /// <summary>从格值生成 64 位哈希的便捷式（直接借用被测纯函数，其正确性由独立断言覆盖）。</summary>
    private static ulong HashOf(byte[] gray) => SimilarPhotoFinder.AverageHash(gray);

    [Fact]
    public void AverageHash_FlatAndAboveMean_AllOnes()
    {
        Assert.Equal(ulong.MaxValue, SimilarPhotoFinder.AverageHash(FlatGray(128)));
    }

    [Fact]
    public void AverageHash_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => SimilarPhotoFinder.AverageHash(new byte[63]));
    }

    [Fact]
    public void HammingDistance_IdenticalZero_OneBitDiffersOne()
    {
        Assert.Equal(0, SimilarPhotoFinder.HammingDistance(0, 0));
        Assert.Equal(64, SimilarPhotoFinder.HammingDistance(0, ulong.MaxValue));
        Assert.Equal(1, SimilarPhotoFinder.HammingDistance(0b1000, 0b1001));
    }

    [Fact]
    public async Task Find_SamePictureDifferentFormats_GroupsTogether()
    {
        // 同一画面的连拍：一张平灰，一张只有个别格偏移（距离 0）——归同组。
        var a = FlatGray(100);
        var b = FlatGray(100); // 完全相同画面（不同文件、不同压缩）
        var c = FlatGray(200); // 亮一档但均匀 → 均值哈希同值 → 距离 0
        var grids = new FakeGrids(
            ("/p/a.jpg", 100, 100, a),
            ("/p/b.jpg", 100, 100, b),
            ("/p/c.jpg", 400, 300, c));
        var finder = new SimilarPhotoFinder(grids);

        var groups = await finder.FindAsync(new[] { Entry("/p/a.jpg"), Entry("/p/b.jpg"), Entry("/p/c.jpg") });

        var group = Assert.Single(groups);
        // 组内按分辨率降序：c（400×300）排前，a/b（100×100）随后。
        Assert.Equal("/p/c.jpg", group.Photos[0].FullPath);
        Assert.Equal(3, group.Photos.Count);
    }

    [Fact]
    public async Task Find_DissimilarImages_NoGroup()
    {
        // 明暗各半棋盘 vs 全白 → 哈希约半数位不同，远超阈值。
        var checker = new byte[64];
        for (int i = 0; i < 64; i++) checker[i] = (i % 2 == 0) ? (byte)255 : (byte)0;
        var grids = new FakeGrids(
            ("/p/x.png", 10, 10, checker),
            ("/p/y.png", 10, 10, FlatGray(255)));
        var finder = new SimilarPhotoFinder(grids);

        var groups = await finder.FindAsync(new[] { Entry("/p/x.png"), Entry("/p/y.png") });

        Assert.Empty(groups);
    }

    [Fact]
    public async Task Find_ChainOfSimilar_MergesIntoOneGroup()
    {
        // 连拍成串口径：A~B 距离 1、B~C 距离 1、A~C 距离 2，都在阈值内 → 一组三张。
        // 格值只有 0/255 两档时，翻转单格即在均值哈希上翻转对应位（其余格距阈值远，不受均值漂移影响）。
        byte[] Flip(byte[] src, int bit) { var g = (byte[])src.Clone(); g[bit] = g[bit] == 0 ? (byte)255 : (byte)0; return g; }
        var baseGray = new byte[64];
        for (int i = 0; i < 64; i++) baseGray[i] = (byte)(i < 32 ? 255 : 0);
        var b = Flip(baseGray, 0);
        var c = Flip(b, 1);

        var grids = new FakeGrids(
            ("/p/a.jpg", 10, 10, baseGray),
            ("/p/b.jpg", 10, 10, b),
            ("/p/c.jpg", 10, 10, c));
        var finder = new SimilarPhotoFinder(grids);

        var groups = await finder.FindAsync(
            new[] { Entry("/p/a.jpg"), Entry("/p/b.jpg"), Entry("/p/c.jpg") },
            new SimilarPhotoOptions { HammingThreshold = 2 });

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Photos.Count);
    }

    [Fact]
    public async Task Find_NonImageExtensions_SkippedBeforeDecode()
    {
        var grids = new FakeGrids(("/p/a.jpg", 10, 10, FlatGray(100)));
        var finder = new SimilarPhotoFinder(grids);

        await finder.FindAsync(new[] { Entry("/p/a.jpg"), Entry("/p/a.txt"), Entry("/p/a.dll") });

        Assert.Equal(1, grids.DecodeCalls); // 非图片扩展名不解码，直接过滤
    }

    [Fact]
    public async Task Find_UndecodableFiles_CountedAsSkipped()
    {
        var grids = new FakeGrids(("/p/good.jpg", 10, 10, FlatGray(100)));
        var finder = new SimilarPhotoFinder(grids);

        var groups = await finder.FindAsync(new[] { Entry("/p/good.jpg"), Entry("/p/broken.jpg") });

        Assert.Empty(groups); // 单张不成组
        Assert.Equal(2, grids.DecodeCalls);
    }

    [Fact]
    public async Task Find_ProgressReportsProcessedAndSkipped()
    {
        var grids = new FakeGrids(
            ("/p/a.jpg", 10, 10, FlatGray(100)),
            ("/p/b.jpg", 10, 10, FlatGray(100)),
            ("/p/bad.jpg", 0, 0, Array.Empty<byte>())); // 空 ≠ 64 格 → 按不可解跳过
        var finder = new SimilarPhotoFinder(grids);
        SimilarPhotoScanProgress? last = null;
        // 同步收集器：Progress<T> 回调走 SynchronizationContext 排队，断言前未必已执行，这里用直调实现消除竞态
        var progress = new SyncProgressCollector(p => last = p);

        await finder.FindAsync(new[] { Entry("/p/a.jpg"), Entry("/p/b.jpg"), Entry("/p/bad.jpg") }, null, progress);

        Assert.NotNull(last);
        Assert.Equal(3, last!.Processed);
        Assert.Equal(3, last.Total);
        Assert.Equal(1, last.Skipped);
    }

    /// <summary>同步进度收集器：IProgress 直调，回调与断言同序，消除排队竞态。</summary>
    private sealed class SyncProgressCollector : IProgress<SimilarPhotoScanProgress>
    {
        private readonly Action<SimilarPhotoScanProgress> _onNext;
        public SyncProgressCollector(Action<SimilarPhotoScanProgress> onNext) => _onNext = onNext;
        public void Report(SimilarPhotoScanProgress value) => _onNext(value);
    }

    [Fact]
    public async Task Find_Cancelled_ThrowsNothingSilent()
    {
        var grids = new FakeGrids(("/p/a.jpg", 10, 10, FlatGray(100)));
        var finder = new SimilarPhotoFinder(grids);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => finder.FindAsync(new[] { Entry("/p/a.jpg") }, null, null, cts.Token));
    }

    [Fact]
    public void GroupByThreshold_SingletonsNotReported()
    {
        var f1 = new PhotoFingerprint("/p/a.jpg", 1, 10, 10, 0b1111);
        var f2 = new PhotoFingerprint("/p/b.jpg", 1, 10, 10, 0b0000); // 距离 4

        var groups = SimilarPhotoFinder.GroupByThreshold(new[] { f1, f2 }, threshold: 3);

        Assert.Empty(groups); // 超阈值不归组，单张永不报
    }
}
