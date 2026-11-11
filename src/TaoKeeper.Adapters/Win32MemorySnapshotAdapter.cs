using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>
/// Win32 内存快照适配器（MemorySnapshot 的 Windows 真机来源）：
/// GlobalMemoryStatusEx 读物理内存；页面文件仅在系统显式配置时可读，
/// 系统托管/读不到时按记录契约传 null（界面如实显示「系统托管」，不编数）。
/// </summary>
public sealed class Win32MemorySnapshotAdapter
{
    [StructLayout(LayoutKind.Sequential)]
    private class MemoryStatus
    {
        public uint Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

    /// <summary>取一次真实内存快照；调用失败抛出（调用方按未测态如实兜底，不假数据）。</summary>
    public MemorySnapshot Capture()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("内存快照仅在 Windows 真机可用。");
        var status = new MemoryStatus();
        if (!GlobalMemoryStatusEx(status))
            throw new InvalidOperationException("GlobalMemoryStatusEx 调用失败。");
        return new MemorySnapshot(
            DateTimeOffset.UtcNow,
            (long)status.TotalPhys,
            (long)status.AvailPhys,
            PageFileTotalBytes: null, // 系统托管页面文件不编数；显式配置口径留待真机核验后回填
            PageFileUsedBytes: null);
    }

    /// <summary>线程池承载快照（避免界面线程阻塞）。</summary>
    public Task<MemorySnapshot> CaptureAsync(CancellationToken cancellation = default)
        => Task.Run(Capture, cancellation);
}
