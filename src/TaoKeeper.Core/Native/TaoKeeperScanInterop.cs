using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TaoKeeper.Core.Native;

/// <summary>C ABI 形态的 $FILE_NAME 解析结果（字段顺序与 native interop.cpp 的 TkMftFileName 逐一对齐，改动须两端同步）。</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TkMftFileNameNative
{
    public ulong ParentRef;
    public ulong Created100Ns;
    public ulong Modified100Ns;
    public ulong Access100Ns;
    public ulong AllocatedSize;
    public ulong RealSize;
    public uint IsDirectory;
    public uint Reserved;
    internal fixed byte NameUtf8[768]; // NUL 结尾 UTF-8（NTFS 名最长 255 UTF-16 字符）

    /// <summary>解码 NUL 结尾 UTF-8 名字。</summary>
    public string GetNameUtf8()
    {
        fixed (byte* p = NameUtf8)
        {
            int n = 0;
            while (n < 768 && p[n] != 0) n++;
            return Encoding.UTF8.GetString(p, n);
        }
    }
}

/// <summary>C ABI 形态的顺序遍历单条产出（记录号 + 文件名属性）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TkMftEntryNative
{
    public ulong RecordNo;
    public TkMftFileNameNative Info;

    /// <summary>转托管模型：剥序后的记录号与文件名字段。</summary>
    public (ulong RecordNo, ulong ParentRef, string Name, ulong RealSize, ulong AllocatedSize,
            bool IsDirectory, ulong Created100Ns, ulong Modified100Ns, ulong Access100Ns) ToManaged() =>
        (TaoKeeperScanNative.MftRecordNumber(RecordNo), TaoKeeperScanNative.MftRecordNumber(Info.ParentRef),
         Info.GetNameUtf8(), Info.RealSize, Info.AllocatedSize, Info.IsDirectory != 0,
         Info.Created100Ns, Info.Modified100Ns, Info.Access100Ns);
}

/// <summary>C ABI 形态的单条目录聚合产出（与 interop.cpp 的 TkMftAggItem 逐一对齐，改动须两端同步）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TkMftAggItemNative
{
    public ulong RecordNo;
    public ulong RecursiveSize;
    public ulong FileCount;
}

/// <summary>
/// TaoKeeperScan 原生库互操作（C++ 扫描核心的 C ABI 导出层）。
/// 库文件按「向上找 native/TaoKeeperScan/build」定位（沙箱与仓内结构），可用环境变量
/// TAOKEEPER_SCAN_NATIVE 直接指定；加载失败时 IsAvailable=false，调用方退回纯托管路径，
/// 逻辑层任何地方不得依赖原生可用性（真机缺失 native 库不能白屏，要给出可见的降级说明）。
/// </summary>
public static class TaoKeeperScanNative
{
    private static readonly object Gate = new();
    private static bool _resolverInstalled;
    private static IntPtr _library;

    /// <summary>最近一次加载失败的说明（诊断用，不弹用户）。</summary>
    public static string? LastLoadError;

    /// <summary>原生库是否已就绪（惰性解析，进程内只解析一次；按候选路径主动定位加载）。</summary>
    public static bool IsAvailable
    {
        get
        {
            InstallResolver();
            if (_library != IntPtr.Zero) return true;
            // NativeLibrary.TryLoad 按名加载不走 DllImportResolver，必须手动按候选路径定位
            foreach (var candidate in CandidatePaths())
            {
                if (!File.Exists(candidate)) continue;
                if (NativeLibrary.TryLoad(candidate, out _library)) return true;
                LastLoadError = candidate;
            }
            return false;
        }
    }

    private static void InstallResolver()
    {
        lock (Gate)
        {
            if (_resolverInstalled) return;
            _resolverInstalled = true;
            NativeLibrary.SetDllImportResolver(typeof(TaoKeeperScanNative).Assembly, Resolve);
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        foreach (var candidate in CandidatePaths())
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                _library = handle;
                return handle;
            }
            LastLoadError = candidate;
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var env = Environment.GetEnvironmentVariable("TAOKEEPER_SCAN_NATIVE");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;

        var nativeName = OperatingSystem.IsWindows() ? "TaoKeeperScan.dll"
            : OperatingSystem.IsMacOS() ? "libTaoKeeperScan.dylib"
            : "libTaoKeeperScan.so";

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var buildDir = Path.Combine(dir.FullName, "native", "TaoKeeperScan", "build");
            yield return Path.Combine(buildDir, nativeName);
            // Windows 上 CMake 默认 VS 生成器把产物放 build\Release\（Debug 同理），一并纳入候选
            yield return Path.Combine(buildDir, "Release", nativeName);
            yield return Path.Combine(buildDir, "Debug", nativeName);
        }
    }

    // ---- P/Invoke 绑定（符号名与 interop.cpp 的 extern "C" 一一对应） ----

    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_record_number", ExactSpelling = true)]
    public static extern ulong MftRecordNumber(ulong refValue);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_parse_mft_file_name", ExactSpelling = true)]
    private static extern unsafe int ParseMftFileNameNative(byte* data, nuint len, TkMftFileNameNative* result);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_walk_mft_records", ExactSpelling = true)]
    private static extern unsafe nuint WalkMftRecordsNative(byte* data, nuint len, nuint recordSize,
        TkMftEntryNative* outBuf, nuint outCap, nuint* needed, nuint* skipped);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_aggregate_dir_sizes", ExactSpelling = true)]
    private static extern unsafe int AggregateDirSizesNative(TkMftEntryNative* entries, nuint count,
        ulong rootRecord, TkMftAggItemNative* outBuf, nuint outCap,
        nuint* needed, ulong* orphanSize, ulong* orphanFiles);

    // 流式解码绑定（internal：仅 MftStreamSession 会话类使用，调用方不得绕过会话生命周期）
    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_stream_create", ExactSpelling = true)]
    internal static extern IntPtr MftStreamCreate(nuint recordSize);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_stream_set_start_offset", ExactSpelling = true)]
    private static extern int MftStreamSetStartOffset(IntPtr handle, ulong bytes);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_stream_feed", ExactSpelling = true)]
    private static extern unsafe nuint MftStreamFeed(IntPtr handle, byte* data, nuint len, nuint* skipped);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_stream_take", ExactSpelling = true)]
    private static extern unsafe nuint MftStreamTake(IntPtr handle, TkMftEntryNative* outBuf, nuint outCap);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_stream_pending", ExactSpelling = true)]
    private static extern ulong MftStreamPending(IntPtr handle);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_stream_next_record_no", ExactSpelling = true)]
    private static extern ulong MftStreamNextRecordNo(IntPtr handle);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_stream_destroy", ExactSpelling = true)]
    private static extern void MftStreamDestroy(IntPtr handle);

    // 分批增量聚合绑定（internal：仅 MftIncrementalAggregatorSession 会话类使用）
    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_agg_incr_create", ExactSpelling = true)]
    internal static extern IntPtr MftAggIncrCreate(ulong rootRecord);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_agg_incr_feed", ExactSpelling = true)]
    private static extern unsafe int MftAggIncrFeed(IntPtr handle, TkMftEntryNative* entries, nuint count);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_agg_incr_snapshot", ExactSpelling = true)]
    private static extern unsafe nuint MftAggIncrSnapshot(IntPtr handle, TkMftAggItemNative* outBuf, nuint outCap,
        nuint* needed, ulong* orphanSize, ulong* orphanFiles);

    [DllImport("TaoKeeperScan", EntryPoint = "tk_mft_agg_incr_destroy", ExactSpelling = true)]
    private static extern void MftAggIncrDestroy(IntPtr handle);

    /// <summary>喂入一批条目；返回喂入条数（句柄失效或原生拒绝返回 -1，空批次为 0 成本）。</summary>
    internal static unsafe long FeedMftAggIncr(IntPtr handle, TkMftEntryNative[] entries)
    {
        if (handle == IntPtr.Zero) return -1;
        if (entries.Length == 0) return 0;
        fixed (TkMftEntryNative* p = entries)
        {
            return MftAggIncrFeed(handle, p, (nuint)entries.Length) == 1 ? entries.Length : -1;
        }
    }

    /// <summary>当前快照（非破坏）：aggregates 输出 ≤capacity 条（记录号升序），needed 输出全部条数
    /// （超过 capacity 需扩缓冲重试），orphan 输出孤儿合计；句柄失效返回 -1。</summary>
    internal static unsafe long SnapshotMftAggIncr(IntPtr handle, TkMftAggItemNative[] buffer, int capacity,
        out long needed, out ulong orphanSize, out ulong orphanFiles)
    {
        needed = 0;
        orphanSize = 0;
        orphanFiles = 0;
        if (handle == IntPtr.Zero || capacity <= 0 || buffer.Length < capacity) return -1;
        nuint needN = 0;
        ulong orphanB = 0, orphanC = 0;
        nuint written;
        fixed (TkMftAggItemNative* outBuf = buffer)
        {
            written = MftAggIncrSnapshot(handle, outBuf, (nuint)capacity, &needN, &orphanB, &orphanC);
        }
        needed = (long)needN;
        orphanSize = orphanB;
        orphanFiles = orphanC;
        return (long)written;
    }

    /// <summary>销毁增量聚合会话句柄（幂等，空句柄安全）。</summary>
    internal static void DestroyMftAggIncr(IntPtr handle)
    {
        if (handle != IntPtr.Zero) MftAggIncrDestroy(handle);
    }

    /// <summary>流式建流；recordSize 无效时原生侧返回空句柄，调用方按建流失败处理。</summary>
    internal static IntPtr TryCreateMftStream(int recordSize) =>
        recordSize <= 0 ? IntPtr.Zero : MftStreamCreate((nuint)recordSize);

    /// <summary>设置流起始跳过的前缀字节数（仅首次 Feed 前生效）；句柄失效返回 false。</summary>
    internal static bool TrySetMftStreamStartOffset(IntPtr handle, ulong bytes) =>
        handle != IntPtr.Zero && MftStreamSetStartOffset(handle, bytes) == 1;

    /// <summary>喂入一块字节；skipped 输出本块坏记录数，返回喂完后的待取条数（句柄失效返回 -1）。</summary>
    internal static unsafe long FeedMftStream(IntPtr handle, ReadOnlySpan<byte> chunk, out long skipped)
    {
        skipped = 0;
        if (handle == IntPtr.Zero) return -1;
        nuint skipN = 0;
        long pending;
        fixed (byte* p = chunk)
        {
            pending = (long)MftStreamFeed(handle, p, (nuint)chunk.Length, &skipN);
        }
        skipped = (long)skipN;
        return pending;
    }

    /// <summary>取走至多 capacity 条待取条目；返回实际取走条数（句柄失效返回 -1）。</summary>
    internal static unsafe long TakeMftStream(IntPtr handle, TkMftEntryNative[] buffer, int capacity)
    {
        if (handle == IntPtr.Zero || capacity <= 0 || buffer.Length < capacity) return -1;
        nuint taken;
        fixed (TkMftEntryNative* outBuf = buffer)
        {
            taken = MftStreamTake(handle, outBuf, (nuint)capacity);
        }
        return (long)taken;
    }

    /// <summary>待取条目数（句柄失效返回 -1）。</summary>
    internal static long QueryMftStreamPending(IntPtr handle) =>
        handle == IntPtr.Zero ? -1 : (long)MftStreamPending(handle);

    /// <summary>下一条记录将获得的记录号，坏记录也占号（句柄失效返回 -1）。</summary>
    internal static long QueryMftStreamNextRecordNo(IntPtr handle) =>
        handle == IntPtr.Zero ? -1L : unchecked((long)MftStreamNextRecordNo(handle));

    /// <summary>释放流句柄；空句柄安全（原生侧 delete 空指针由绑定层拦截，不外发）。</summary>
    internal static void DestroyMftStream(IntPtr handle)
    {
        if (handle != IntPtr.Zero) MftStreamDestroy(handle);
    }

    /// <summary>托管封装：解析单条记录；返回 null=拒绝（坏魔数/损坏/无 $FILE_NAME）。</summary>
    public static unsafe TkMftFileNameNative? TryParseMftFileName(ReadOnlySpan<byte> record)
    {
        if (!IsAvailable || record.IsEmpty) return null;
        TkMftFileNameNative result;
        fixed (byte* p = record)
        {
            if (ParseMftFileNameNative(p, (nuint)record.Length, &result) != 1) return null;
        }
        return result;
    }

    /// <summary>
    /// 托管封装：顺序遍历 $MFT 字节块。返回有效条目（最多 capacity 条，不足会按 needed 提示重试）；
    /// needed 输出全部有效条数，skipped 输出坏块数；库不可用返回 null（调用方走降级路径）。
    /// </summary>
    public static unsafe bool TryWalkMftRecords(byte[] blob, int recordSize, int capacity,
        out IReadOnlyList<TkMftEntryNative> entries, out long needed, out long skipped)
    {
        entries = Array.Empty<TkMftEntryNative>();
        needed = 0;
        skipped = 0;
        if (!IsAvailable || blob.Length == 0 || recordSize <= 0 || capacity <= 0) return false;

        var buffer = new TkMftEntryNative[capacity];
        nuint walked;
        fixed (byte* data = blob)
        fixed (TkMftEntryNative* outBuf = buffer)
        {
            nuint needN = 0, skipN = 0;
            walked = WalkMftRecordsNative(data, (nuint)blob.Length, (nuint)recordSize,
                outBuf, (nuint)capacity, &needN, &skipN);
            needed = (long)needN;
            skipped = (long)skipN;
        }
        var list = new List<TkMftEntryNative>((int)walked);
        for (nuint i = 0; i < walked; i++) list.Add(buffer[i]);
        entries = list;
        return true;
    }

    /// <summary>
    /// 托管封装：空间分析聚合（体积沿父链上卷到每级目录）。entries 为遍历产出的同布局数组；
    /// needed 输出全部目录聚合条数（超过 capacity 时需扩大缓冲重试，按记录号升序带出），
    /// orphan 输出父链缺失或成环部分的体积与文件数；库不可用或参数无效返回 false（调用方走降级路径）。
    /// </summary>
    public static unsafe bool TryAggregateDirSizes(TkMftEntryNative[] entries, ulong rootRecord,
        int capacity, out IReadOnlyList<TkMftAggItemNative> aggregates,
        out long needed, out ulong orphanSize, out ulong orphanFiles)
    {
        aggregates = Array.Empty<TkMftAggItemNative>();
        needed = 0;
        orphanSize = 0;
        orphanFiles = 0;
        if (!IsAvailable || capacity <= 0) return false;

        var buffer = new TkMftAggItemNative[capacity];
        nint rc;
        fixed (TkMftEntryNative* inBuf = entries)
        fixed (TkMftAggItemNative* outBuf = buffer)
        {
            nuint needN = 0;
            ulong orphanBytes = 0, orphanCnt = 0;
            rc = AggregateDirSizesNative(inBuf, (nuint)entries.Length, rootRecord,
                outBuf, (nuint)capacity, &needN, &orphanBytes, &orphanCnt);
            needed = (long)needN;
            orphanSize = orphanBytes;
            orphanFiles = orphanCnt;
        }
        if (rc != 1) return false;

        var list = new List<TkMftAggItemNative>((int)needed);
        for (int i = 0; i < needed && i < capacity; i++) list.Add(buffer[i]);
        aggregates = list;
        return true;
    }
}
