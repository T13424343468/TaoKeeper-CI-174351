using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Services;

namespace TaoKeeper.Core.Modules;

/// <summary>
/// 相似照片识别器（R-P10-2a）。
/// 判据=8×8 均值感知哈希（aHash）的海明距离，比字节判重高一档：同一画面不同压缩/尺寸的
/// 连拍与重复导出能归到一组；字节级完全相同由重复文件卡负责，两卡互不越界。
/// 只读：不修改、不移动、不删除任何文件；取消即零残留（未产出任何结果）。
/// </summary>
public sealed class SimilarPhotoFinder
{
    private readonly IImageGridSource _grids;

    public SimilarPhotoFinder(IImageGridSource grids)
    {
        _grids = grids ?? throw new ArgumentNullException(nameof(grids));
    }

    /// <summary>
    /// 在已扫描的文件清单中识别相似照片组。
    /// 流程：扩展名过滤 → 逐个解码建纹（不可解的如实跳过）→ 阈值内两两连边 → 连通分量成组。
    /// 复杂度 O(n²) 仅在指纹之间，64 位整数异或，单组百万像素解码不在此层。
    /// </summary>
    public async Task<IReadOnlyList<SimilarPhotoGroup>> FindAsync(
        IReadOnlyList<FileEntry> files,
        SimilarPhotoOptions? options = null,
        IProgress<SimilarPhotoScanProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        options ??= new SimilarPhotoOptions();

        var started = DateTimeOffset.UtcNow;
        var candidates = files
            .Where(f => options.ImageExtensions.Contains(
                System.IO.Path.GetExtension(f.FullPath)))
            .ToList();

        var fingerprints = new List<PhotoFingerprint>(candidates.Count);
        var skipped = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var grid = await _grids.DecodeAsync(candidates[i].FullPath, cancellation).ConfigureAwait(false);
            if (grid is { Width: > 0, Height: > 0 } g && g.Gray.Length == 64)
                fingerprints.Add(new PhotoFingerprint(
                    candidates[i].FullPath, candidates[i].SizeBytes,
                    g.Width, g.Height, AverageHash(g.Gray)));
            else
                skipped++;

            if ((i + 1) % 16 == 0 || i == candidates.Count - 1)
                progress?.Report(new SimilarPhotoScanProgress(i + 1, candidates.Count, skipped, 0));
        }

        var groups = GroupByThreshold(fingerprints, options.HammingThreshold);
        if (groups.Count > 0)
            progress?.Report(new SimilarPhotoScanProgress(candidates.Count, candidates.Count, skipped, groups.Count));
        return groups;
    }

    /// <summary>8×8 均值感知哈希：格均值以上记 1，以下记 0，拼成 64 位。</summary>
    public static ulong AverageHash(byte[] gray)
    {
        ArgumentNullException.ThrowIfNull(gray);
        if (gray.Length != 64)
            throw new ArgumentException("感知哈希只吃 8×8=64 格灰度，格式由解码契约保证。", nameof(gray));

        double mean = 0;
        foreach (var v in gray) mean += v;
        mean /= gray.Length;

        ulong hash = 0;
        for (int i = 0; i < 64; i++)
            if (gray[i] >= mean)
                hash |= 1UL << i;
        return hash;
    }

    /// <summary>64 位海明距离（异或后数 1 位）。</summary>
    public static int HammingDistance(ulong a, ulong b)
    {
        ulong x = a ^ b;
        int count = 0;
        while (x != 0)
        {
            x &= x - 1; // 每轮消掉最低位的 1
            count++;
        }
        return count;
    }

    /// <summary>
    /// 按海明距离阈值连通分组：A~B、B~C 距离都在阈值内则 A/B/C 同组（连拍成串的真实形态）。
    /// 只有 ≥2 张才成组，单张不报；并查集增量合并，无递归深栈。
    /// </summary>
    public static IReadOnlyList<SimilarPhotoGroup> GroupByThreshold(
        IReadOnlyList<PhotoFingerprint> fingerprints, int threshold)
    {
        if (threshold < 0)
            throw new ArgumentOutOfRangeException(nameof(threshold));

        var parent = new int[fingerprints.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]]; // 路径折半，无栈
                x = parent[x];
            }
            return x;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }

        for (int i = 0; i < fingerprints.Count; i++)
            for (int j = i + 1; j < fingerprints.Count; j++)
                if (HammingDistance(fingerprints[i].Hash, fingerprints[j].Hash) <= threshold)
                    Union(i, j);

        var buckets = new Dictionary<int, List<PhotoFingerprint>>();
        for (int i = 0; i < fingerprints.Count; i++)
        {
            int root = Find(i);
            if (!buckets.TryGetValue(root, out var list))
                buckets[root] = list = new List<PhotoFingerprint>();
            list.Add(fingerprints[i]);
        }

        return buckets.Values
            .Where(g => g.Count >= 2)
            .Select(g => (SimilarPhotoGroup)new SimilarPhotoGroup(
                g.OrderByDescending(p => p.PixelCount).ThenBy(p => p.FullPath, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(g => g.Photos.Count)
            .ThenBy(g => g.Photos[0].FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
