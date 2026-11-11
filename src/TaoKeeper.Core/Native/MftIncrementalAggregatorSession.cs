using System;
using System.Collections.Generic;

namespace TaoKeeper.Core.Native;

/// <summary>
/// 分批增量聚合会话（大卷内存峰值优化）：包装 tk_mft_agg_incr_* C ABI 的句柄生命周期。
/// 与 MftStreamSession 配套的流式口径：流式解码分批 Take 出条目后喂入本会话，
/// 只常驻目录节点（真实卷约占条目 1%），文件体积沿父链计入后即弃——
/// 快照只输出连通到卷根的目录聚合（记录号升序）与孤儿合计，与一次性聚合同构。
/// 用法：TryCreate → 循环 TryFeed（父记录可晚于子）→ 需要时 TrySnapshot（非破坏可重复）→ Dispose。
/// 会话非线程安全（单线程流式喂入）；句柄销毁后所有调用安全返回失败值；
/// 口径差异（畸形数据）：父记录为文件记录时按孤儿处理，不沿文件链透传（正常卷无差异）。
/// 库不可用时 TryCreate 返回 null，调用方走纯托管降级路径。
/// </summary>
public sealed class MftIncrementalAggregatorSession : IDisposable
{
    private IntPtr _handle;
    private int _disposeFlag;

    private MftIncrementalAggregatorSession(IntPtr handle, ulong rootRecord)
    {
        _handle = handle;
        RootRecord = rootRecord;
    }

    /// <summary>建会话；rootRecord 为卷根记录号（NTFS 通常 5）。库不可用返回 null。</summary>
    public static MftIncrementalAggregatorSession? TryCreate(ulong rootRecord)
    {
        if (!TaoKeeperScanNative.IsAvailable) return null;
        var handle = TaoKeeperScanNative.MftAggIncrCreate(rootRecord);
        return handle == IntPtr.Zero ? null : new MftIncrementalAggregatorSession(handle, rootRecord);
    }

    /// <summary>卷根记录号（建会话时固定）。</summary>
    public ulong RootRecord { get; }

    /// <summary>句柄是否已释放（释放后一切调用安全拒绝）。</summary>
    public bool IsDisposed => Volatile.Read(ref _disposeFlag) != 0;

    /// <summary>
    /// 喂入一批已解析条目（顺序任意，父记录可晚于子记录出现；批次须来自流式解码等唯一来源，
    /// 重复喂入同一条目按重复计数）。返回实际喂入条数；已销毁或原生拒绝返回 -1。
    /// </summary>
    public long TryFeed(IReadOnlyList<TkMftEntryNative> entries)
    {
        if (IsDisposed || entries.Count == 0) return IsDisposed ? -1 : 0;
        var h = _handle;
        var buffer = entries as TkMftEntryNative[] ?? new List<TkMftEntryNative>(entries).ToArray();
        return TaoKeeperScanNative.FeedMftAggIncr(h, buffer);
    }

    /// <summary>
    /// 当前快照（非破坏，可重复）：aggregates 输出全部连通目录的递归聚合（记录号升序），
    /// orphanSize/orphanFiles 输出父链缺失或成环部分的体积与文件数合计。
    /// 返回实际输出条数；已销毁、参数无效或原生拒绝返回 -1（needed 置 -1 供调用方扩缓冲重试）。
    /// </summary>
    public long TrySnapshot(int capacity, out IReadOnlyList<TkMftAggItemNative> aggregates,
        out long needed, out ulong orphanSize, out ulong orphanFiles)
    {
        aggregates = Array.Empty<TkMftAggItemNative>();
        needed = -1;
        orphanSize = 0;
        orphanFiles = 0;
        if (IsDisposed || capacity <= 0) return -1;
        var h = _handle;
        var buffer = new TkMftAggItemNative[capacity];
        var written = TaoKeeperScanNative.SnapshotMftAggIncr(h, buffer, capacity,
            out needed, out orphanSize, out orphanFiles);
        if (written < 0) return -1;
        var list = new List<TkMftAggItemNative>((int)written);
        for (var i = 0; i < written; i++) list.Add(buffer[i]);
        aggregates = list;
        return written;
    }

    /// <summary>释放句柄；幂等，重复调用与析构路径均安全。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeFlag, 1) != 0) return;
        var h = _handle;
        _handle = IntPtr.Zero;
        TaoKeeperScanNative.DestroyMftAggIncr(h);
        GC.SuppressFinalize(this);
    }

    ~MftIncrementalAggregatorSession() => Dispose();
}
