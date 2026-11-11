using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Native;

namespace TaoKeeper.Core.Modules;

/// <summary>
/// $MFT 分块读取抽象：按序读一块 $MFT 字节。Windows 适配层用卷句柄顺序读实现（本沙箱不编译）；
/// 测试与工具用 StreamMftVolumeReader 适配任意顺序流。
/// </summary>
public interface IMftVolumeReader
{
    /// <summary>读一块到缓冲，返回实际读取字节数；流结束返回 0。</summary>
    int Read(Span<byte> buffer);
}

/// <summary>把任意顺序流适配为 $MFT 分块读取器（仅一次正向顺序读，不支持回退）。</summary>
public sealed class StreamMftVolumeReader : IMftVolumeReader
{
    private readonly Stream _source;

    public StreamMftVolumeReader(Stream source) =>
        _source = source ?? throw new ArgumentNullException(nameof(source));

    public int Read(Span<byte> buffer) => _source.Read(buffer);
}

/// <summary>
/// $MFT 流式扫描编排：MftStreamSession 分块喂入 → 攒批取走 → 末尾目录聚合。
/// 只读零写动作；原生库不可用时 TryScan 返回 null，调用方退回便携目录遍历路径（不得依赖原生可用性）。
/// 会话按单线程读循环设计，本类非线程安全。
/// </summary>
public sealed class MftStreamingScanner
{
    /// <summary>
    /// 流式扫描 $MFT 字节流。
    /// recordSize 为 MFT 记录大小（通常 1024）；rootRecord 为聚合根记录号（NTFS 根目录为 5）；
    /// startOffsetBytes 跳过流起始前缀（须是第一条记录前的非记录字节）；readChunkBytes 为每次读取块长；
    /// takeBatch 为攒批取走批量（待取达到该值即取走，防队列无界积存）。
    /// 返回 null=原生库不可用（降级）；参数非法抛 ArgumentOutOfRangeException。
    /// </summary>
    public MftStreamScanResult? TryScan(
        int recordSize,
        IMftVolumeReader reader,
        ulong rootRecord = 5,
        long startOffsetBytes = 0,
        int readChunkBytes = 1 << 20,
        int takeBatch = 4096,
        IProgress<MftStreamScanProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (recordSize <= 0) throw new ArgumentOutOfRangeException(nameof(recordSize));
        if (startOffsetBytes < 0) throw new ArgumentOutOfRangeException(nameof(startOffsetBytes));
        if (readChunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(readChunkBytes));
        if (takeBatch <= 0) throw new ArgumentOutOfRangeException(nameof(takeBatch));

        using var session = MftStreamSession.TryCreate(recordSize);
        if (session is null) return null; // 原生库不可用：走便携降级路径

        if (startOffsetBytes > 0 && !session.SetStartOffset(unchecked((ulong)startOffsetBytes)))
            throw new InvalidOperationException("流式会话设置起始偏移失败（会话已失效）");

        var buffer = new byte[readChunkBytes];
        var entries = new List<MftEntryInfo>();
        long bytesRead = 0, skipped = 0;

        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var n = reader.Read(buffer);
            if (n <= 0) break;
            bytesRead += n;
            if (!session.TryFeed(buffer.AsSpan(0, n), out var blockSkipped, out _)) break;
            skipped += blockSkipped;
            while (session.Pending >= takeBatch) // 满批即取，防队列无界积存
                TakeBatch(session, takeBatch, entries);
            progress?.Report(new MftStreamScanProgress(
                entries.Count, bytesRead, skipped, session.NextRecordNo));
        }
        // 收尾：队列余量全部取走
        while (session.Pending > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            TakeBatch(session, takeBatch, entries);
        }
        progress?.Report(new MftStreamScanProgress(
            entries.Count, bytesRead, skipped, session.NextRecordNo)); // 终态：余量排空后的最终计数

        Aggregate(session, entries, rootRecord, out var aggregates, out var orphanSize, out var orphanFiles);

        return new MftStreamScanResult(
            entries, aggregates, bytesRead, skipped, orphanSize, orphanFiles);
    }

    private static void TakeBatch(MftStreamSession session, int takeBatch, List<MftEntryInfo> entries)
    {
        if (session.Pending <= 0) return;
        if (!session.TryTake(takeBatch, out var batch))
            throw new InvalidOperationException("流式会话取走失败（会话已失效）");
        foreach (var e in batch)
        {
            var (recordNo, parentNo, name, real, alloc, isDir, created, modified, access) = e.ToManaged();
            entries.Add(new MftEntryInfo(
                recordNo, parentNo, name, real, alloc, isDir,
                FromFileTime(created), FromFileTime(modified), FromFileTime(access)));
        }
    }

    /// <summary>末尾聚合：紧凑列表回填平坦数组一次调用（容量按 needed 自扩重试）。</summary>
    private static void Aggregate(
        MftStreamSession session,
        List<MftEntryInfo> entries, ulong rootRecord,
        out IReadOnlyList<MftDirAggregate> aggregates, out ulong orphanSize, out ulong orphanFiles)
    {
        aggregates = Array.Empty<MftDirAggregate>();
        orphanSize = 0;
        orphanFiles = 0;
        if (entries.Count == 0) return;

        var flat = new TkMftEntryNative[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            flat[i].RecordNo = e.RecordNo;
            flat[i].Info.ParentRef = e.ParentRecordNo; // 已剥序，与聚合按记录号键控一致
            flat[i].Info.RealSize = e.RealSize;
            flat[i].Info.AllocatedSize = e.AllocatedSize;
            flat[i].Info.IsDirectory = e.IsDirectory ? 1u : 0u;
        }

        var capacity = Math.Max(16, entries.Count);
        while (true)
        {
            var ok = TaoKeeperScanNative.TryAggregateDirSizes(flat, rootRecord, capacity,
                out var items, out var needed, out orphanSize, out orphanFiles);
            if (!ok)
                throw new InvalidOperationException("流式扫描目录聚合失败（原生调用被拒）");
            if (items.Count >= needed) // 容量足够，全部带出
            {
                aggregates = items.Select(a => new MftDirAggregate(a.RecordNo, a.RecursiveSize, a.FileCount)).ToList();
                return;
            }
            var enlarged = needed > int.MaxValue - 1 ? int.MaxValue : (int)needed;
            if (enlarged <= capacity)
                throw new InvalidOperationException("流式扫描目录聚合容量无法扩大（needed 超出上限）");
            capacity = enlarged;
        }
    }

    private static DateTimeOffset FromFileTime(ulong value100Ns)
    {
        try
        {
            return DateTimeOffset.FromFileTime(unchecked((long)value100Ns));
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.FromFileTime(0); // 非法时间戳按纪元兜底，不因脏数据中断扫描
        }
    }
}
