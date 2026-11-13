using System;
using System.Collections.Generic;

namespace TaoKeeper.Core.Modules;

/// <summary>一张图片的感知指纹：8×8 均值哈希 + 原始分辨率。</summary>
public sealed record PhotoFingerprint(
    string FullPath,
    long SizeBytes,
    int Width,
    int Height,
    ulong Hash)
{
    /// <summary>像素总量，作为「建议保留」的默认判据（组内分辨率最高者）。</summary>
    public long PixelCount => (long)Width * Height;
}

/// <summary>相似照片分组结果：一组互为相似（感知哈希海明距离 ≤ 阈值）的图。</summary>
public sealed record SimilarPhotoGroup(IReadOnlyList<PhotoFingerprint> Photos);

/// <summary>相似照片扫描的选项。</summary>
public sealed class SimilarPhotoOptions
{
    /// <summary>按感知哈希分组的海明距离阈值（0=仅逐位全同；默认 5，覆盖连拍/缩略差异）。</summary>
    public int HammingThreshold { get; init; } = 5;

    /// <summary>视为图片的扩展名集合（小写）；大小写不敏感匹配，不写死在遍历处。</summary>
    public ISet<string> ImageExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };
}

/// <summary>相似照片扫描进度。</summary>
public sealed record SimilarPhotoScanProgress(int Processed, int Total, int Skipped, int Grouped);
