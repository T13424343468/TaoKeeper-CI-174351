using System;
using System.Collections.Generic;

namespace TaoKeeper.Core.Native;

/// <summary>
/// $MFT 连续读取流式解码会话（真机分块直读用）：包装 tk_mft_stream_* C ABI 的句柄生命周期。
/// 用法：TryCreate →（可选）SetStartOffset → 循环 Read 一块就 TryFeed 一块、Pending 起来就 TryTake 分批
/// → 用完 Dispose。会话非线程安全（按单线程读循环设计）；句柄销毁后所有调用安全返回失败值，
/// Dispose 可重复调用（防重复释放）。库不可用时 TryCreate 返回 null，调用方走纯托管降级路径。
/// </summary>
public sealed class MftStreamSession : IDisposable
{
    private IntPtr _handle;
    private int _disposeFlag;

    private MftStreamSession(IntPtr handle, int recordSize)
    {
        _handle = handle;
        RecordSize = recordSize;
    }

    /// <summary>建流；recordSize 为 MFT 记录大小（通常 1024）。库不可用或参数无效返回 null。</summary>
    public static MftStreamSession? TryCreate(int recordSize)
    {
        if (!TaoKeeperScanNative.IsAvailable) return null;
        var handle = TaoKeeperScanNative.TryCreateMftStream(recordSize);
        return handle == IntPtr.Zero ? null : new MftStreamSession(handle, recordSize);
    }

    /// <summary>MFT 记录大小（字节）。</summary>
    public int RecordSize { get; }

    /// <summary>句柄是否已释放（释放后一切调用安全拒绝）。</summary>
    public bool IsDisposed => Volatile.Read(ref _disposeFlag) != 0;

    /// <summary>设置流起始要跳过的前缀字节数（须在首次 Feed 前设置；首次 Feed 后再设不生效）。已销毁返回 false。</summary>
    public bool SetStartOffset(ulong bytes)
    {
        var h = _handle;
        return !IsDisposed && TaoKeeperScanNative.TrySetMftStreamStartOffset(h, bytes);
    }

    /// <summary>
    /// 喂入一块按序读到的字节（块大小任意，跨块残记录自动拼接）。
    /// skipped 输出本块新解析出的坏记录数，pendingAfterFeed 输出喂完后的待取条数；已销毁返回 false。
    /// </summary>
    public unsafe bool TryFeed(ReadOnlySpan<byte> chunk, out long skipped, out long pendingAfterFeed)
    {
        skipped = 0;
        pendingAfterFeed = -1;
        if (IsDisposed) return false;
        var h = _handle;
        var pending = TaoKeeperScanNative.FeedMftStream(h, chunk, out skipped);
        if (pending < 0) return false;
        pendingAfterFeed = pending;
        return true;
    }

    /// <summary>
    /// 取走至多 maxCount 条待取条目（超出 Pending 时取全部，取走即出队）。
    /// entries 输出实际取走的条目；已销毁或 maxCount 非法返回 false。
    /// </summary>
    public bool TryTake(int maxCount, out IReadOnlyList<TkMftEntryNative> entries)
    {
        entries = Array.Empty<TkMftEntryNative>();
        if (IsDisposed || maxCount <= 0) return false;
        var h = _handle;
        var buffer = new TkMftEntryNative[maxCount];
        var taken = TaoKeeperScanNative.TakeMftStream(h, buffer, maxCount);
        if (taken < 0) return false;
        var list = new List<TkMftEntryNative>((int)taken);
        for (var i = 0; i < taken; i++) list.Add(buffer[i]);
        entries = list;
        return true;
    }

    /// <summary>待取条目数（未及时 Take 时积存在内部队列）；已销毁返回 -1。</summary>
    public long Pending
    {
        get
        {
            var h = _handle;
            return IsDisposed ? -1 : TaoKeeperScanNative.QueryMftStreamPending(h);
        }
    }

    /// <summary>下一条记录将获得的记录号（坏记录也占号，进度回传用）；已销毁返回 -1。</summary>
    public long NextRecordNo
    {
        get
        {
            var h = _handle;
            return IsDisposed ? -1 : TaoKeeperScanNative.QueryMftStreamNextRecordNo(h);
        }
    }

    /// <summary>释放句柄；幂等，重复调用与析构路径均安全。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeFlag, 1) != 0) return;
        var h = _handle;
        _handle = IntPtr.Zero;
        TaoKeeperScanNative.DestroyMftStream(h);
        GC.SuppressFinalize(this);
    }

    ~MftStreamSession() => Dispose();
}
