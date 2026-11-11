using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Native;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// 分批增量聚合会话测试（库可用时执行）：与一次性聚合对拍（同数据同口径）、
/// 乱序批次、快照非破坏、孤儿记账、会话生命周期与降级路径。
/// 库不可用时仅验证降级路径（TryCreate 返回 null），原生断言自动跳过。
/// </summary>
public class MftIncrementalAggregatorSessionTests
{
    private static bool LibraryAvailable => TaoKeeperScanNative.IsAvailable;

    /// <summary>构造一组正常树条目（父引用带序列号，喂入路径须剥序）：卷根 5 下两级目录与 5 个文件。</summary>
    private static List<TkMftEntryNative> MakeTree() =>
        new()
        {
            Make(5, 5, 0, isDir: true, seq: 1),
            Make(10, 5, 0, isDir: true, seq: 2),
            Make(11, 5, 0, isDir: true, seq: 3),
            Make(20, 10, 0, isDir: true, seq: 4),
            Make(30, 20, 100, isDir: false, seq: 5),
            Make(31, 20, 200, isDir: false, seq: 6),
            Make(32, 10, 50, isDir: false, seq: 7),
            Make(33, 5, 70, isDir: false, seq: 8),
            Make(34, 11, 30, isDir: false, seq: 9),
        };

    private static TkMftEntryNative Make(ulong record, ulong parent, ulong size, bool isDir, ulong seq)
    {
        var entry = new TkMftEntryNative
        {
            RecordNo = record,
            Info = new TkMftFileNameNative
            {
                ParentRef = parent | (seq << 48), // 低 48 位记录号、高 16 位序列号
                RealSize = size,
                IsDirectory = (uint)(isDir ? 1 : 0),
            },
        };
        return entry;
    }

    /// <summary>乱序洗牌（父后于子），模拟流式解码的真实顺序无保证。</summary>
    private static List<TkMftEntryNative> Shuffled(List<TkMftEntryNative> entries, int seed)
    {
        var rng = new Random(seed);
        var list = entries.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    [Fact]
    public void 库不可用时建会话返回空_降级路径成立()
    {
        if (LibraryAvailable) return; // 原生可用的环境走对拍用例
        using var session = MftIncrementalAggregatorSession.TryCreate(5);
        Assert.Null(session);
    }

    [Fact]
    public void 正常树乱序分批喂入_快照与一次性聚合对拍一致()
    {
        if (!LibraryAvailable) return;

        using var session = MftIncrementalAggregatorSession.TryCreate(5);
        Assert.NotNull(session);
        var entries = MakeTree();

        // 按 3 条一批乱序喂入
        var shuffled = Shuffled(entries, seed: 7);
        for (var i = 0; i < shuffled.Count; i += 3)
            Assert.Equal(Math.Min(3, shuffled.Count - i), session!.TryFeed(shuffled.Skip(i).Take(3).ToList()));

        AssertSnapshotParity(session!, entries);
    }

    [Fact]
    public void 快照非破坏可重复_继续喂入后推进()
    {
        if (!LibraryAvailable) return;

        using var session = MftIncrementalAggregatorSession.TryCreate(5);
        Assert.NotNull(session);
        var entries = MakeTree();

        Assert.Equal(5, session!.TryFeed(entries.Take(5).ToList())); // 喂一半
        AssertSnapshotParity(session, entries.Take(5).ToList());

        Assert.Equal(4, session.TryFeed(entries.Skip(5).ToList()));
        AssertSnapshotParity(session, entries);
    }

    [Fact]
    public void 父链缺失与成环_孤儿记账与一次性聚合一致()
    {
        if (!LibraryAvailable) return;

        using var session = MftIncrementalAggregatorSession.TryCreate(5);
        Assert.NotNull(session);
        var entries = new List<TkMftEntryNative>
        {
            Make(5, 5, 0, isDir: true, seq: 1),
            Make(10, 5, 0, isDir: true, seq: 2),
            Make(30, 10, 100, isDir: false, seq: 3),
            Make(31, 40, 60, isDir: false, seq: 4),   // 父 40 不存在 → 孤儿
            Make(50, 60, 0, isDir: true, seq: 5),     // 目录 50 父 60 缺失 → 断开子树
            Make(51, 50, 20, isDir: false, seq: 6),   // 经 50 计孤儿
            Make(70, 80, 0, isDir: true, seq: 7),
            Make(80, 70, 0, isDir: true, seq: 8),     // 70↔80 成环
            Make(81, 70, 40, isDir: false, seq: 9),   // 环内文件 → 孤儿
        };

        Assert.Equal(entries.Count, session!.TryFeed(Shuffled(entries, seed: 3)));
        AssertSnapshotParity(session, entries);

        session.TrySnapshot(64, out _, out var needed, out var orphanSize, out var orphanFiles);
        Assert.Equal(120ul, orphanSize); // 60 + 20 + 40
        Assert.Equal(3ul, orphanFiles);
        Assert.True(needed >= 0);
    }

    [Fact]
    public void 会话生命周期_销毁后安全拒绝与幂等释放()
    {
        if (!LibraryAvailable) return;

        var session = MftIncrementalAggregatorSession.TryCreate(5);
        Assert.NotNull(session);
        Assert.False(session!.IsDisposed);

        Assert.Equal(9, session.TryFeed(MakeTree()));
        session.Dispose();
        Assert.True(session.IsDisposed);
        Assert.Equal(-1, session.TryFeed(MakeTree()));                 // 销毁后喂入拒绝
        Assert.Equal(-1, session.TrySnapshot(16, out _, out _, out _, out _)); // 销毁后快照拒绝
        session.Dispose();                                             // 幂等释放
        GC.KeepAlive(session);
    }

    [Fact]
    public void 空批次与零快照容量_安全零成本()
    {
        if (!LibraryAvailable) return;

        using var session = MftIncrementalAggregatorSession.TryCreate(5);
        Assert.NotNull(session);
        Assert.Equal(0, session!.TryFeed(Array.Empty<TkMftEntryNative>()));
        Assert.Equal(-1, session.TrySnapshot(0, out _, out _, out _, out _)); // 非法容量拒绝
    }

    /// <summary>快照与一次性聚合对拍：条目集、递归值、孤儿合计逐项一致。</summary>
    private static void AssertSnapshotParity(MftIncrementalAggregatorSession session,
        IReadOnlyList<TkMftEntryNative> entries)
    {
        Assert.Equal(1, session.TrySnapshot(64, out var aggregates, out var needed, out var orphanSize, out var orphanFiles) >= 0 ? 1 : 0);

        // 一次性聚合对拍（同数据同口径；增量对「父为文件记录」的畸形结构按孤儿处理，正常树无差异）
        var oneShotOk = TaoKeeperScanNative.TryAggregateDirSizes(entries.ToArray(), 5, 64,
            out var oneShot, out var oneNeeded, out var oneOrphanSize, out var oneOrphanFiles);
        Assert.True(oneShotOk);

        Assert.Equal(oneNeeded, needed);
        Assert.Equal(oneOrphanSize, orphanSize);
        Assert.Equal(oneOrphanFiles, orphanFiles);
        Assert.Equal(oneShot.Count, aggregates.Count);
        for (var i = 0; i < aggregates.Count; i++)
        {
            Assert.Equal(oneShot[i].RecordNo, aggregates[i].RecordNo);
            Assert.Equal(oneShot[i].RecursiveSize, aggregates[i].RecursiveSize);
            Assert.Equal(oneShot[i].FileCount, aggregates[i].FileCount);
        }
    }
}
