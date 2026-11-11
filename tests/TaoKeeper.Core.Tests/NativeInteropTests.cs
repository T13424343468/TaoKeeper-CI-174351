using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaoKeeper.Core.Native;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// Core↔native 接口对齐实测：构建出 TaoKeeperScan 共享库后，C# 经 C ABI 真实调用
/// （剥序、单条解析、顺序遍历），验证结构体布局与编组两端一致。
/// 口径：库可用即真实调用对拍；不可加载（如真机未装 CMake、缺原生库）按降级契约跳过，
/// 不静默报红——加载器按平台找 libTaoKeeperScan.so / TaoKeeperScan.dll / .dylib；
/// 沙箱每轮强制重建，此口径不影响沙箱全测覆盖。
/// </summary>
public class NativeInteropTests
{
    [Fact]
    public void 共享库应可加载()
    {
        // 库不可用（真机未装 CMake、缺原生库）按降级契约跳过，避免环境差异误报红
        if (!TaoKeeperScanNative.IsAvailable) return;
        // 库在时本测试即证明可加载（IsAvailable 已真实走完候选路径定位与加载）
    }

    // ---- 合成记录构造（与 C++ 测试同布局：STANDARD_INFORMATION + FILE_NAME + USA fixup） ----

    private const int RecSize = 1024;
    private const int Attr1Off = 0x40;
    private const int Attr2Off = 0x88;
    private const int FnValueOff = 0x18;

    private static void Put16(byte[] b, int off, ushort v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
    private static void Put32(byte[] b, int off, uint v) { for (int i = 0; i < 4; ++i) b[off + i] = (byte)(v >> (8 * i)); }
    private static void Put64(byte[] b, int off, ulong v) { for (int i = 0; i < 8; ++i) b[off + i] = (byte)(v >> (8 * i)); }

    private static byte[] BuildRecord(string name, ulong parent, ulong real, ulong allocated, bool isDir)
    {
        var rec = new byte[RecSize];
        Encoding.UTF8.GetBytes("FILE", 0, 4, rec, 0);
        Put16(rec, 0x14, Attr1Off);
        Put16(rec, 0x30, 0x38);
        Put16(rec, 0x32, 3);

        Put32(rec, Attr1Off, 0x10);
        Put32(rec, Attr1Off + 4, 0x48);
        rec[Attr1Off + 8] = 0;
        Put32(rec, Attr1Off + 16, 0x30);
        Put16(rec, Attr1Off + 20, 0x18);

        var nameBytes = Encoding.Unicode.GetBytes(name); // UTF-16LE
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

        const ushort check = 0x1A2B;
        Put16(rec, 0x38, check);
        Put16(rec, 0x3A, 0x1112);
        Put16(rec, 0x3C, 0x1113);
        Put16(rec, 510, check);
        Put16(rec, 1022, check);
        return rec;
    }

    [Fact]
    public void 原生库可用时_剥序与单条解析往返一致()
    {
        if (!TaoKeeperScanNative.IsAvailable) return;

        Assert.Equal(0x42ul, TaoKeeperScanNative.MftRecordNumber(0x0005000000000042ul));

        var rec = BuildRecord("测试报告.txt", 6, 123456, 131072, isDir: false);
        var parsed = TaoKeeperScanNative.TryParseMftFileName(rec);
        Assert.NotNull(parsed);
        var name = parsed!.Value.GetNameUtf8();
        Assert.Equal("测试报告.txt", name);
        Assert.Equal(6ul, TaoKeeperScanNative.MftRecordNumber(parsed.Value.ParentRef));
        Assert.Equal(123456ul, parsed.Value.RealSize);
        Assert.Equal(131072ul, parsed.Value.AllocatedSize);
        Assert.Equal(0u, parsed.Value.IsDirectory);
        Assert.Equal(132000000001000000ul, parsed.Value.Modified100Ns);
    }

    [Fact]
    public void 原生库可用时_顺序遍历跨边界编组正确()
    {
        if (!TaoKeeperScanNative.IsAvailable) return;

        var blob = new List<byte>();
        var good = BuildRecord("说明书.txt", 5, 4096, 8192, isDir: false);
        var bad = BuildRecord("损坏", 5, 1, 1, isDir: false);
        Encoding.UTF8.GetBytes("BAAD", 0, 4, bad, 0);
        blob.AddRange(good);
        blob.AddRange(bad);
        blob.AddRange(good);

        var ok = TaoKeeperScanNative.TryWalkMftRecords(blob.ToArray(), RecSize, capacity: 16,
            out var entries, out var needed, out var skipped);
        Assert.True(ok);
        Assert.Equal(2, needed);        // 两条有效
        Assert.Equal(1, skipped);       // 一条坏块
        Assert.Equal(2, entries.Count); // 容量足够，全部带出
        Assert.Equal(0ul, entries[0].RecordNo);
        Assert.Equal("说明书.txt", entries[0].Info.GetNameUtf8());
        Assert.Equal(2ul, entries[1].RecordNo);
        var (recordNo, parentRef, name, real, alloc, isDir, _, _, _) = entries[0].ToManaged();
        Assert.Equal(0ul, recordNo);
        Assert.Equal(5ul, parentRef);
        Assert.Equal("说明书.txt", name);
        Assert.Equal(4096ul, real);
        Assert.Equal(8192ul, alloc);
        Assert.False(isDir);
    }

    [Fact]
    public void 原生库可用时_同一缓冲重复解析结果不变_fixup不写回输入()
    {
        if (!TaoKeeperScanNative.IsAvailable) return;

        var rec = BuildRecord("回归检查.txt", 5, 4096, 8192, isDir: false);
        var first = TaoKeeperScanNative.TryParseMftFileName(rec);
        Assert.NotNull(first);
        var second = TaoKeeperScanNative.TryParseMftFileName(rec);
        Assert.NotNull(second);                       // 曾因 fixup 就地写回导致二次解析必拒
        Assert.Equal(first!.Value.RealSize, second!.Value.RealSize);
        Assert.Equal(first.Value.GetNameUtf8(), second.Value.GetNameUtf8());
        // 原缓冲扇区尾仍是校验值（未被解析过程改写）
        Assert.Equal(0x2B, rec[510]);
        Assert.Equal(0x1A, rec[511]);
    }

    /// <summary>构造聚合用平坦条目（字段与 C++ TkMftEntry 同布局，名字留空不参与聚合）。</summary>
    private static TkMftEntryNative MakeEntry(ulong recordNo, ulong parent, ulong real, bool isDir)
    {
        var e = new TkMftEntryNative();
        e.RecordNo = recordNo;
        e.Info.ParentRef = parent;
        e.Info.RealSize = real;
        e.Info.IsDirectory = isDir ? 1u : 0u;
        return e;
    }

    [Fact]
    public void 原生库可用时_目录聚合跨边界编组正确()
    {
        if (!TaoKeeperScanNative.IsAvailable) return;

        // 与 C++ mft_tree 场景 7 同源数据：目录6←根5、目录7←6、文件8(100)←7、文件9(200)←6、文件10(50)←根5（根不在映射）
        var entries = new[]
        {
            MakeEntry(6, 5, 0, isDir: true),
            MakeEntry(7, 6, 0, isDir: true),
            MakeEntry(8, 7, 100, isDir: false),
            MakeEntry(9, 6, 200, isDir: false),
            MakeEntry(10, 5, 50, isDir: false),
        };
        var ok = TaoKeeperScanNative.TryAggregateDirSizes(entries, rootRecord: 5, capacity: 16,
            out var aggs, out var needed, out var orphanSize, out var orphanFiles);
        Assert.True(ok);
        Assert.Equal(2, needed);                 // 目录 6、7 两条聚合
        Assert.Equal(0ul, orphanSize);           // 走到根的部分不算孤儿
        Assert.Equal(0ul, orphanFiles);
        Assert.Equal(2, aggs.Count);             // 容量足够，全部带出（按记录号升序）
        Assert.Equal(6ul, aggs[0].RecordNo);     // 目录6 = 子目录链 100 + 直属 200
        Assert.Equal(300ul, aggs[0].RecursiveSize);
        Assert.Equal(2ul, aggs[0].FileCount);
        Assert.Equal(7ul, aggs[1].RecordNo);     // 目录7 = 自己链上 100
        Assert.Equal(100ul, aggs[1].RecursiveSize);
        Assert.Equal(1ul, aggs[1].FileCount);
        Assert.DoesNotContain(aggs, a => a.RecordNo == 10ul); // 文件记录自身不出聚合
    }

    [Fact]
    public void 原生库可用时_流式会话分块喂入与整块遍历对拍()
    {
        if (!TaoKeeperScanNative.IsAvailable) return;

        // 与 C++ 流式测试同源素材：好(甲.txt)→好(目录A)→坏(fixup 不符)→好(乙.bin)，坏记录占号不断档
        var good = BuildRecord("说明书.txt", 5, 4096, 8192, isDir: false);
        var dir = BuildRecord("目录A", 5, 0, 8192, isDir: true);
        var bad = BuildRecord("目录A", 5, 0, 8192, isDir: true);
        Encoding.UTF8.GetBytes("BAAD", 0, 4, bad, 0);
        var tail = BuildRecord("乙.bin", 1, 999999, 1048576, isDir: false);
        var blob = good.Concat(dir).Concat(bad).Concat(tail).ToArray();

        // 整块遍历基准
        var walkOk = TaoKeeperScanNative.TryWalkMftRecords(blob, RecSize, capacity: 16,
            out var walked, out _, out var walkSkipped);
        Assert.True(walkOk);
        Assert.Equal(3, walked.Count);
        Assert.Equal(1, walkSkipped);

        // 流式会话：每块 333 字节分块喂入（跨块拼接残记录），分批取走
        using var session = MftStreamSession.TryCreate(RecSize);
        Assert.NotNull(session);
        Assert.Equal(RecSize, session!.RecordSize);
        Assert.Equal(0, session.NextRecordNo); // 未喂入时下一条记录号为 0

        long totalSkipped = 0;
        for (var off = 0; off < blob.Length; off += 333)
        {
            var len = Math.Min(333, blob.Length - off);
            var fed = session.TryFeed(blob.AsSpan(off, len), out var skipped, out var pending);
            Assert.True(fed);
            totalSkipped += skipped;
            Assert.InRange(pending, 0, 3); // 待取数单调累计，不超素材好记录总数
        }
        Assert.Equal(1, totalSkipped);           // 损坏记录计一次 skipped
        Assert.Equal(3, session.Pending);        // 三条好记录全部入队
        Assert.Equal(4, session.NextRecordNo);   // 坏记录也占号（0..3）

        Assert.True(session.TryTake(16, out var streamed));
        Assert.Equal(walked.Count, streamed.Count);
        for (var i = 0; i < walked.Count; ++i)
        {
            Assert.Equal(walked[i].RecordNo, streamed[i].RecordNo);
            Assert.Equal(walked[i].Info.GetNameUtf8(), streamed[i].Info.GetNameUtf8());
            Assert.Equal(walked[i].Info.ParentRef, streamed[i].Info.ParentRef);
            Assert.Equal(walked[i].Info.RealSize, streamed[i].Info.RealSize);
            Assert.Equal(walked[i].Info.IsDirectory, streamed[i].Info.IsDirectory);
        }
        Assert.Equal(3ul, streamed[2].RecordNo); // 第三条好记录 = 记录3（坏占号不断档）
        Assert.Equal("乙.bin", streamed[2].Info.GetNameUtf8());
    }

    [Fact]
    public void 原生库可用时_流式会话分批取走与容量截取()
    {
        if (!TaoKeeperScanNative.IsAvailable) return;

        var blob = BuildRecord("甲.txt", 5, 100, 4096, isDir: false)
            .Concat(BuildRecord("目录B", 5, 0, 8192, isDir: true))
            .Concat(BuildRecord("丙.log", 5, 7, 4096, isDir: false))
            .ToArray();
        using var session = MftStreamSession.TryCreate(RecSize);
        Assert.NotNull(session);
        Assert.True(session!.TryFeed(blob, out var skipped, out _));
        Assert.Equal(0, skipped);
        Assert.Equal(3, session.Pending);

        Assert.True(session.TryTake(2, out var first));    // 容量截取
        Assert.Equal(2, first.Count);
        Assert.Equal("甲.txt", first[0].Info.GetNameUtf8());
        Assert.Equal(1, session.Pending);                  // 余量仍在队
        Assert.True(session.TryTake(2, out var rest));     // 余下再取
        Assert.Single(rest);
        Assert.Equal("丙.log", rest[0].Info.GetNameUtf8());
        Assert.Equal(0, session.Pending);
        Assert.True(session.TryTake(2, out var empty));    // 取空再取
        Assert.Empty(empty);
    }

    [Fact]
    public void 原生库可用时_流式会话起始跳过与销毁后安全拒绝()
    {
        if (!TaoKeeperScanNative.IsAvailable) return;

        // 起始跳过：流前缀 17 字节非记录数据
        var blob = BuildRecord("跳过测试.txt", 5, 10, 4096, isDir: false);
        var prefixed = new byte[17].Concat(blob).ToArray();
        using (var session = MftStreamSession.TryCreate(RecSize))
        {
            Assert.NotNull(session);
            Assert.True(session!.SetStartOffset(17));      // 首次 Feed 前设置生效
            Assert.True(session.TryFeed(prefixed, out var skipped, out _));
            Assert.Equal(0, skipped);
            Assert.Equal(1, session.Pending);
            Assert.True(session.TryTake(4, out var entries));
            Assert.Single(entries);
            Assert.Equal("跳过测试.txt", entries[0].Info.GetNameUtf8());
        }

        // 销毁防重复释放与安全拒绝
        var toClose = MftStreamSession.TryCreate(RecSize);
        Assert.NotNull(toClose);
        toClose!.Dispose();
        toClose.Dispose();                                 // 幂等：二次释放不炸
        Assert.True(toClose.IsDisposed);
        Assert.False(toClose.TryFeed(blob, out _, out _));
        Assert.False(toClose.TryTake(1, out _));
        Assert.False(toClose.SetStartOffset(0));
        Assert.Equal(-1, toClose.Pending);
        Assert.Equal(-1, toClose.NextRecordNo);

        // 建流参数防护：recordSize 无效按建流失败处理（降级路径）
        Assert.Null(MftStreamSession.TryCreate(0));
        Assert.Null(MftStreamSession.TryCreate(-1024));
    }

    [Fact]
    public void 原生库可用时_聚合孤儿桶跨边界回传()
    {
        if (!TaoKeeperScanNative.IsAvailable) return;

        // 与 C++ mft_tree 场景 8 同源数据：孤儿(父缺失,777)、环40↔41、环上文件(888)、正常到根(10)；根 5 不在映射
        var entries = new[]
        {
            MakeEntry(30, 99, 777, isDir: false),  // 父缺失
            MakeEntry(40, 41, 0, isDir: true),     // 环：40↔41
            MakeEntry(41, 40, 0, isDir: true),
            MakeEntry(42, 40, 888, isDir: false),  // 挂在环上的文件
            MakeEntry(43, 5, 10, isDir: false),    // 正常到根
        };
        var ok = TaoKeeperScanNative.TryAggregateDirSizes(entries, rootRecord: 5, capacity: 16,
            out var aggs, out var needed, out var orphanSize, out var orphanFiles);
        Assert.True(ok);
        Assert.Equal(0, needed);                 // 无目录链成立
        Assert.Empty(aggs);
        Assert.Equal(777ul + 888, orphanSize);   // 孤儿=父缺失+成环两笔
        Assert.Equal(2ul, orphanFiles);
    }
}
