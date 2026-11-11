using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Services;

namespace TaoKeeper.Core.Modules;

/// <summary>重复文件识别结果：一组互为重复的文件。</summary>
public sealed record DuplicateGroup(
    string Sha256,
    long SizeBytes,
    IReadOnlyList<FileEntry> Files)
{
    /// <summary>若全部移入回收站后可释放的空间（保留组内一份）。</summary>
    public long ReclaimableBytes => SizeBytes * Math.Max(0, Files.Count - 1);
}

/// <summary>重复文件识别的选项。</summary>
public sealed class DuplicateOptions
{
    /// <summary>小于该字节数的文件直接跳过（默认 1 字节，即不跳过）。</summary>
    public long MinFileSizeBytes { get; init; } = 1;

    /// <summary>首轮抽读头部字节数（默认 4KB）；抽读不同者直接判异，不再全量哈希。</summary>
    public int HeadSampleBytes { get; init; } = 4096;

    /// <summary>单文件全量哈希上限（字节），超过则跳过并在结果中标注未识别；0 表示无上限。</summary>
    public long MaxHashFileSizeBytes { get; init; } = 2L * 1024 * 1024 * 1024;
}

/// <summary>
/// 重复文件识别器（模块 M1 之三）。
/// 零误报原则：只有全文件 SHA-256 完全相同才判定重复；文件名与修改时间一律不作为判据。
/// 只读：不修改、不移动、不删除任何文件。
/// </summary>
public sealed class DuplicateFileFinder
{
    private readonly IFileHasher _hasher;
    private readonly IHeadSampler? _sampler;

    public DuplicateFileFinder(IFileHasher hasher, IHeadSampler? sampler = null)
    {
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _sampler = sampler;
    }

    /// <summary>
    /// 在已扫描的文件清单中识别重复文件组。
    /// 流程：按体积分桶（体积不同必不重复）→ 头部抽样初筛（可选）→ 全量 SHA-256 终判。
    /// </summary>
    public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(
        IReadOnlyList<FileEntry> files,
        DuplicateOptions? options = null,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        options ??= new DuplicateOptions();
        var started = DateTimeOffset.UtcNow;

        // 第一步：体积分桶——体积不同必然不重复，一个哈希都不用算。
        var bySize = files
            .Where(f => f.SizeBytes >= options.MinFileSizeBytes)
            .GroupBy(f => f.SizeBytes)
            .Where(g => g.Count() > 1)
            .ToList();

        int total = bySize.Sum(g => g.Count());
        int done = 0;
        void Tick()
        {
            done++;
            if (done % 64 == 0)
                progress?.Report(new DuplicateScanProgress(done, total, DuplicatePhase.SizeBuckets, started));
        }

        var groups = new ConcurrentBag<DuplicateGroup>();

        // 第二步：逐桶处理——头部抽样初筛 + 全量哈希终判。
        await Task.Run(async () =>
        {
            foreach (var sizeBucket in bySize)
            {
                cancellation.ThrowIfCancellationRequested();

                // 抽样初筛：头部字节不同者直接出局，不进全量哈希。
                List<FileEntry> candidates = sizeBucket.ToList();
                if (_sampler is not null && options.HeadSampleBytes > 0)
                {
                    var headBuckets = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
                    foreach (var f in candidates)
                    {
                        Tick();
                        string head;
                        try { head = await _sampler.HeadHexAsync(f.FullPath, options.HeadSampleBytes, cancellation); }
                        catch (IOException) { continue; } // 不可读的文件不参与判定
                        if (!headBuckets.TryGetValue(head, out var bucket)) headBuckets[head] = bucket = new();
                        bucket.Add(f);
                    }
                    candidates = headBuckets.Values.Where(b => b.Count > 1).SelectMany(b => b).ToList();
                }

                // 全量哈希终判：唯一仲裁标准。
                var byHash = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
                foreach (var f in candidates)
                {
                    Tick();
                    if (options.MaxHashFileSizeBytes > 0 && f.SizeBytes > options.MaxHashFileSizeBytes) continue;
                    string hash;
                    try { hash = await _hasher.HashAsync(f.FullPath, cancellation); }
                    catch (IOException) { continue; } // 不可读的文件不参与判定
                    if (!byHash.TryGetValue(hash, out var bucket)) byHash[hash] = bucket = new();
                    bucket.Add(f);
                }

                foreach (var same in byHash.Values.Where(b => b.Count > 1))
                {
                    groups.Add(new DuplicateGroup(
                        byHash.First(kv => kv.Value == same).Key,
                        same[0].SizeBytes,
                        same.OrderBy(f => f.LastModifiedUtc).ToList()));
                }
            }
        }, cancellation);

        progress?.Report(new DuplicateScanProgress(done, total, DuplicatePhase.Finished, started));
        return groups
            .OrderByDescending(g => g.ReclaimableBytes)
            .ToList();
    }
}

/// <summary>重复识别进度。</summary>
public sealed record DuplicateScanProgress(
    int ProcessedFiles,
    int TotalFiles,
    DuplicatePhase Phase,
    DateTimeOffset StartedUtc);

/// <summary>重复识别阶段。</summary>
public enum DuplicatePhase { SizeBuckets, Finished }
