using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Models;

namespace TaoKeeper.Core.Modules;

/// <summary>
/// 磁盘扫描器（模块 M1）。
/// 快扫为默认模式；实现必须可取消、可汇报进度，且绝不修改任何文件（只读扫描）。
/// </summary>
public interface IDiskScanner
{
    /// <summary>扫描模式。</summary>
    ScanMode Mode { get; }

    /// <summary>
    /// 扫描一个或多个根目录，返回文件清单与目录统计。
    /// cancellation 触发后必须尽快返回，已完成的部分按 Completed 语义丢弃（不产出半成品结果对象）。
    /// </summary>
    Task<ScanResult> ScanAsync(
        IReadOnlyList<string> roots,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default);
}

/// <summary>扫描模式：快扫（默认，跳过已知噪声目录）或普通扫（全量）。</summary>
public enum ScanMode { Quick, Full }

public sealed class ScanOptions
{
    /// <summary>快扫跳过的目录名（大小写不敏感）。默认含 Windows、$RECYCLE.BIN、System Volume Information、node_modules、.git 等。</summary>
    public ISet<string> SkipDirectoryNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "$RECYCLE.BIN", "System Volume Information",
            "node_modules", ".git", ".svn", "__pycache__"
        };

    /// <summary>大文件阈值（字节），默认 512MB。</summary>
    public long LargeFileThresholdBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>是否计算哈希（重复文件识别需要）。快扫默认不计算。</summary>
    public bool ComputeHashes { get; init; }

    /// <summary>单文件哈希上限（字节），超过则跳过哈希防止长尾阻塞；0 表示无上限。</summary>
    public long MaxHashFileSizeBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public static ScanOptions Defaults(ScanMode mode) => new()
    {
        ComputeHashes = mode == ScanMode.Full
    };
}

/// <summary>一次扫描的完整结果。</summary>
public sealed record ScanResult(
    ScanMode Mode,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<DirectoryStats> Directories,
    IReadOnlyList<FileEntry> LargeFiles,
    long TotalBytes,
    long TotalFiles);
