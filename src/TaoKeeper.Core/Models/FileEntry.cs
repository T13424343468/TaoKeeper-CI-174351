using System;

namespace TaoKeeper.Core.Models;

/// <summary>扫描得到的一个文件记录。</summary>
public sealed record FileEntry(
    string FullPath,
    string Name,
    string Directory,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    FileAttributesSlim Attributes,
    string? Sha256 = null)
{
    public bool IsLargeFile(long thresholdBytes) => SizeBytes >= thresholdBytes;
}

/// <summary>最小化属性集，避免直接依赖 IO 枚举便于测试。</summary>
[Flags]
public enum FileAttributesSlim
{
    None = 0,
    Hidden = 1,
    System = 2,
    ReadOnly = 4
}

/// <summary>目录尺寸统计。</summary>
public sealed record DirectoryStats(string FullPath, long TotalBytes, long FileCount);
