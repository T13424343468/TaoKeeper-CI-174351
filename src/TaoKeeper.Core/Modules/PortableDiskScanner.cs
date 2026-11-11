using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Services;

namespace TaoKeeper.Core.Modules;

/// <summary>
/// 可移植目录遍历扫描器（Linux/测试验证用；Windows 真机接 C++ 核心的 NTFS MFT 直读路径）。
/// 只读：任何实现都不得写、移动或删除文件。
/// </summary>
public sealed class PortableDiskScanner : IDiskScanner
{
    private readonly IFileHasher? _hasher;

    public PortableDiskScanner(IFileHasher? hasher = null) => _hasher = hasher;

    public ScanMode Mode { get; private set; } = ScanMode.Quick;

    public async Task<ScanResult> ScanAsync(
        IReadOnlyList<string> roots,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        Mode = options.ComputeHashes ? ScanMode.Full : ScanMode.Quick;
        var started = DateTimeOffset.UtcNow;

        var files = new ConcurrentBag<FileEntry>();
        var dirStats = new ConcurrentBag<DirectoryStats>();
        long seen = 0, bytesSeen = 0;

        var walker = new ParallelOptions { CancellationToken = cancellation };

        await Task.Run(async () =>
        {
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                await WalkDirectoryAsync(
                    root, options, files, dirStats,
                    () => { var n = Interlocked.Increment(ref seen);
                            if (n % 500 == 0) progress?.Report(new ScanProgress(
                                n, Interlocked.Read(ref bytesSeen), root, ScanPhase.Walking));
                            Interlocked.Add(ref bytesSeen, 0); },
                    b => Interlocked.Add(ref bytesSeen, b),
                    cancellation);
            }
        }, cancellation);

        cancellation.ThrowIfCancellationRequested();

        var fileList = files.ToList();
        var large = fileList
            .Where(f => f.SizeBytes >= options.LargeFileThresholdBytes)
            .OrderByDescending(f => f.SizeBytes)
            .ToList();

        return new ScanResult(
            Mode, started, DateTimeOffset.UtcNow,
            fileList, dirStats.ToList(), large,
            fileList.Sum(f => f.SizeBytes), fileList.Count);
    }

    private async Task WalkDirectoryAsync(
        string dir, ScanOptions options,
        ConcurrentBag<FileEntry> files, ConcurrentBag<DirectoryStats> stats,
        Action tick, Action<long> addBytes, CancellationToken cancellation)
    {
        // 目录属性：快扫跳过白名单里的目录名
        var name = Path.GetFileName(dir);
        if (options.SkipDirectoryNames.Contains(name) && dir != Path.GetPathRoot(dir))
            return;

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch (UnauthorizedAccessException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (IOException) { return; }

        long dirBytes = 0, dirCount = 0;
        foreach (var entry in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            FileSystemInfo info;
            try { var fi = new FileInfo(entry); info = fi.Exists ? fi : new DirectoryInfo(entry); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            if (info is DirectoryInfo sub)
            {
                await WalkDirectoryAsync(sub.FullName, options, files, stats, tick, addBytes, cancellation);
            }
            else if (info is FileInfo fi)
            {
                tick();
                addBytes(fi.Length);
                dirBytes += fi.Length;
                dirCount++;
                string? hash = null;
                if (options.ComputeHashes && _hasher is not null
                    && (options.MaxHashFileSizeBytes == 0 || fi.Length <= options.MaxHashFileSizeBytes))
                {
                    try { hash = await _hasher.HashAsync(fi.FullName, cancellation); }
                    catch (IOException) { hash = null; }
                }
                files.Add(new FileEntry(
                    fi.FullName, fi.Name, fi.DirectoryName ?? "",
                    fi.Length,
                    new DateTimeOffset(fi.LastWriteTimeUtc),
                    AttributesFrom(fi),
                    hash));
            }
        }
        stats.Add(new DirectoryStats(dir, dirBytes, dirCount));
    }

    private static FileAttributesSlim AttributesFrom(FileInfo fi)
    {
        var r = FileAttributesSlim.None;
        if (fi.Attributes.HasFlag(System.IO.FileAttributes.Hidden)) r |= FileAttributesSlim.Hidden;
        if (fi.Attributes.HasFlag(System.IO.FileAttributes.System)) r |= FileAttributesSlim.System;
        if (fi.Attributes.HasFlag(System.IO.FileAttributes.ReadOnly)) r |= FileAttributesSlim.ReadOnly;
        return r;
    }
}
