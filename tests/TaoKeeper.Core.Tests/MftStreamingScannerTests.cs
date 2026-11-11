using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// MftStreamingScanner 流式扫描编排实测：假卷读取器按序供块（含 17 字节前缀、坏记录占号），
/// 走真实原生库完成分块喂入→攒批取走→目录聚合；与既有遍历/聚合口径对拍。
/// 断言口径与 NativeInteropTests 一致：库可用即真实调用对拍；不可加载（真机未装 CMake、缺原生库）
/// 按降级契约跳过，不静默报红；沙箱每轮强制重建，此口径不影响沙箱全测覆盖。
/// </summary>
public class MftStreamingScannerTests
{
    private const int RecSize = 1024;
    private const int Attr2Off = 0x88;
    private const int FnValueOff = 0x18;

    private static void Put16(byte[] b, int off, ushort v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
    private static void Put32(byte[] b, int off, uint v) { for (int i = 0; i < 4; ++i) b[off + i] = (byte)(v >> (8 * i)); }
    private static void Put64(byte[] b, int off, ulong v) { for (int i = 0; i < 8; ++i) b[off + i] = (byte)(v >> (8 * i)); }

    private static byte[] BuildRecord(string name, ulong parent, ulong real, ulong allocated, bool isDir)
    {
        var rec = new byte[RecSize];
        Encoding.UTF8.GetBytes("FILE", 0, 4, rec, 0);
        Put16(rec, 0x14, 0x40);
        Put16(rec, 0x30, 0x38);
        Put16(rec, 0x32, 3);
        Put32(rec, 0x40, 0x10);
        Put32(rec, 0x44, 0x48);
        Put32(rec, 0x50, 0x30);
        Put16(rec, 0x54, 0x18);

        var nameBytes = Encoding.Unicode.GetBytes(name);
        Put32(rec, Attr2Off, 0x30);
        Put32(rec, Attr2Off + 4, (uint)(0x18 + 66 + nameBytes.Length));
        rec[Attr2Off + 8] = 0;
        Put32(rec, Attr2Off + 16, (uint)(66 + nameBytes.Length));
        Put16(rec, Attr2Off + 20, FnValueOff);
        int fn = Attr2Off + FnValueOff;
        Put64(rec, fn, parent);
        Put64(rec, fn + 8, 132000000000000000ul);
        Put64(rec, fn + 16, 132000000001000000ul);
        Put64(rec, fn + 40, allocated);
        Put64(rec, fn + 48, real);
        Put32(rec, fn + 56, isDir ? 1u : 0u);
        rec[fn + 64] = (byte)name.Length;
        rec[fn + 65] = 1;
        for (int i = 0; i < name.Length; ++i)
        {
            var ch = Encoding.Unicode.GetBytes(name[i].ToString());
            rec[fn + 66 + i * 2] = ch[0];
            rec[fn + 67 + i * 2] = ch[1];
        }
        Put32(rec, Attr2Off + 0x18 + 66 + nameBytes.Length, 0xFFFFFFFF);

        Put16(rec, 0x38, 0x1A2B);
        Put16(rec, 0x3A, 0x1112);
        Put16(rec, 0x3C, 0x1113);
        Put16(rec, 510, 0x1A2B);
        Put16(rec, 1022, 0x1A2B);
        return rec;
    }

    /// <summary>库不可用时跳过本轮（与降级契约一致），避免真机环境差异误报红；内联判定直接从测试方法返回。</summary>
    private static bool NativeUnavailable() => !TaoKeeper.Core.Native.TaoKeeperScanNative.IsAvailable;

    /// <summary>合成 $MFT 字节流：17 字节前缀 + 6 占位 + 目录树（6←5、7←6、8←7(100)、9←6(200)、10←5(50)）+ 1 坏记录。</summary>
    private static byte[] BuildBlob()
    {
        IEnumerable<byte> blob = new byte[17].Select(b => b); // 0xAB 前缀由调用方填充；此处给 17 个零
        var parts = new List<byte[]>();
        for (var i = 0; i < 6; i++)
            parts.Add(BuildRecord($"占位{i}", 5, 1, 4096, isDir: false));
        parts.Add(BuildRecord("目录六", 5, 0, 8192, isDir: true));
        parts.Add(BuildRecord("目录七", 6, 0, 8192, isDir: true));
        parts.Add(BuildRecord("文件八.txt", 7, 100, 4096, isDir: false));
        parts.Add(BuildRecord("文件九.txt", 6, 200, 4096, isDir: false));
        parts.Add(BuildRecord("文件十.log", 5, 50, 4096, isDir: false));
        var bad = BuildRecord("目录六", 5, 0, 8192, isDir: true);
        Encoding.UTF8.GetBytes("BAAD", 0, 4, bad, 0);
        parts.Add(bad);
        foreach (var p in parts) blob = blob.Concat(p);
        var bytes = blob.ToArray();
        for (var i = 0; i < 17; i++) bytes[i] = 0xAB;
        return bytes;
    }

    private sealed class ChunkedReader : IMftVolumeReader
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _pos;

        public ChunkedReader(byte[] data, int chunk) { _data = data; _chunk = chunk; }

        public int Read(Span<byte> buffer)
        {
            if (_pos >= _data.Length) return 0;
            var n = Math.Min(buffer.Length, Math.Min(_chunk, _data.Length - _pos));
            _data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
    }

    /// <summary>同步进度收集器（Report 即入列，断言确定）。</summary>
    private sealed class CollectingProgress : IProgress<MftStreamScanProgress>
    {
        public List<MftStreamScanProgress> Items { get; } = new();
        public void Report(MftStreamScanProgress value) => Items.Add(value);
    }

    [Fact]
    public void 流式扫描_分块读取与目录聚合对拍()
    {
        if (NativeUnavailable()) return;
        var blob = BuildBlob();
        var scanner = new MftStreamingScanner();

        var result = scanner.TryScan(RecSize, new ChunkedReader(blob, chunk: 4096), // 块长非记录整倍数，逼出跨块拼接
            rootRecord: 5, startOffsetBytes: 17);
        Assert.NotNull(result);

        Assert.Equal(11, result!.Entries.Count);        // 6 占位 + 5 树内 + 坏记录占号不计
        Assert.Equal(1, result.SkippedRecords);         // 坏记录计一次
        Assert.Equal(17L + 12L * RecSize, result.BytesRead);

        // 记录号按位置：占位 0..5、目录六 6、目录七 7、文件八 8、文件九 9、文件十 10
        Assert.Equal("目录六", result.Entries[6].Name);
        Assert.True(result.Entries[6].IsDirectory);
        Assert.Equal(5ul, result.Entries[6].ParentRecordNo);
        Assert.Equal("文件九.txt", result.Entries[9].Name);
        Assert.Equal(200ul, result.Entries[9].RealSize);
        Assert.Equal(6ul, result.Entries[9].ParentRecordNo);

        // 聚合与既有 ABI 测试场景 7 同口径：目录6=100+200、目录7=100；根 5 不在映射不算孤儿
        Assert.Equal(2, result.DirAggregates.Count);
        Assert.Equal(6ul, result.DirAggregates[0].RecordNo);
        Assert.Equal(300ul, result.DirAggregates[0].RecursiveSize);
        Assert.Equal(2ul, result.DirAggregates[0].FileCount);
        Assert.Equal(7ul, result.DirAggregates[1].RecordNo);
        Assert.Equal(100ul, result.DirAggregates[1].RecursiveSize);
        Assert.Equal(1ul, result.DirAggregates[1].FileCount);
        Assert.Equal(0ul, result.OrphanSize);
        Assert.Equal(0ul, result.OrphanFiles);

        // 时间戳：100ns FILETIME → Utc（合成记录写入值 132000000001000000 = 2019-04-06 附近，仅验可解非纪元）
        Assert.True(result.Entries[9].ModifiedUtc.Year > 2000);
    }

    [Fact]
    public void 流式扫描_进度上报与流适配器结果一致()
    {
        if (NativeUnavailable()) return;
        var blob = BuildBlob();
        var scanner = new MftStreamingScanner();

        // 基准：ChunkedReader 结果
        var baseResult = scanner.TryScan(RecSize, new ChunkedReader(blob, chunk: 333),
            rootRecord: 5, startOffsetBytes: 17);
        Assert.NotNull(baseResult);

        // MemoryStream + StreamMftVolumeReader（整流一次可读）应与分块结果一致
        var collector = new CollectingProgress(); // 同步收集（Progress<T> 走同步上下文异步派发，断言终点会不稳定）
        var streamed = scanner.TryScan(RecSize, new StreamMftVolumeReader(new MemoryStream(blob)),
            rootRecord: 5, startOffsetBytes: 17, readChunkBytes: 1 << 20, takeBatch: 4, // 小批量逼攒批取走
            progress: collector);
        Assert.NotNull(streamed);
        Assert.Equal(baseResult!.Entries.Count, streamed!.Entries.Count);
        Assert.Equal(baseResult.SkippedRecords, streamed.SkippedRecords);
        Assert.Equal(baseResult.DirAggregates.Count, streamed.DirAggregates.Count);
        for (var i = 0; i < baseResult.Entries.Count; i++)
        {
            Assert.Equal(baseResult.Entries[i].RecordNo, streamed.Entries[i].RecordNo);
            Assert.Equal(baseResult.Entries[i].Name, streamed.Entries[i].Name);
            Assert.Equal(baseResult.Entries[i].ParentRecordNo, streamed.Entries[i].ParentRecordNo);
        }

        // 进度：同步收集器直接入列；校验终点状态
        Assert.NotEmpty(collector.Items);
        var last = collector.Items[^1];
        Assert.Equal(11, last.RecordsDecoded);
        Assert.Equal(streamed.BytesRead, last.BytesRead);
        Assert.Equal(1, last.SkippedRecords);
        Assert.Equal(12, last.NextRecordNo); // 坏记录占号后下一条为 12
    }

    [Fact]
    public void 流式扫描_空流_参数防护与取消()
    {
        if (NativeUnavailable()) return;
        var scanner = new MftStreamingScanner();

        // 空流：0 条目、0 聚合、无孤儿
        var empty = scanner.TryScan(RecSize, new ChunkedReader(Array.Empty<byte>(), chunk: 4096));
        Assert.NotNull(empty);
        Assert.Empty(empty!.Entries);
        Assert.Empty(empty.DirAggregates);
        Assert.Equal(0ul, empty.OrphanSize);

        // 参数防护
        Assert.Throws<ArgumentNullException>(() => scanner.TryScan(RecSize, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            scanner.TryScan(0, new ChunkedReader(Array.Empty<byte>(), chunk: 4096)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            scanner.TryScan(RecSize, new ChunkedReader(Array.Empty<byte>(), chunk: 4096), startOffsetBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            scanner.TryScan(RecSize, new ChunkedReader(Array.Empty<byte>(), chunk: 4096), readChunkBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            scanner.TryScan(RecSize, new ChunkedReader(Array.Empty<byte>(), chunk: 4096), takeBatch: 0));

        // 取消：预取消令牌在首个读取循环即抛出
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            scanner.TryScan(RecSize, new ChunkedReader(BuildBlob(), chunk: 4096),
                cancellation: cts.Token));
    }
}
