using System;
using System.Collections.Generic;

namespace TaoKeeper.Core.Models;

/// <summary>$MFT 流式扫描进度（仅用于展示，不参与最终结果）。</summary>
public sealed record MftStreamScanProgress(
    long RecordsDecoded,
    long BytesRead,
    long SkippedRecords,
    long NextRecordNo);

/// <summary>
/// 流式扫描得到的一条记录（紧凑托管形态：名字为托管字符串，不含 768 字节原生名字缓冲；
/// 记录号/父记录号均已剥序为纯记录号）。
/// </summary>
public sealed record MftEntryInfo(
    ulong RecordNo,
    ulong ParentRecordNo,
    string Name,
    ulong RealSize,
    ulong AllocatedSize,
    bool IsDirectory,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset AccessUtc);

/// <summary>目录聚合一条产出（体积/文件数沿父链上卷到该目录）。</summary>
public sealed record MftDirAggregate(ulong RecordNo, ulong RecursiveSize, ulong FileCount);

/// <summary>
/// $MFT 流式扫描结果：全量条目 + 目录聚合 + 过程计数。
/// 注：聚合在扫描末尾一次性回填平坦数组执行，百万级条目时内存峰值约「条目数×824 字节」，
/// 聚合完成即释放；分批增量聚合为后续原生 ABI 扩展候选（真机大卷先按此上限评估）。
/// </summary>
public sealed record MftStreamScanResult(
    IReadOnlyList<MftEntryInfo> Entries,
    IReadOnlyList<MftDirAggregate> DirAggregates,
    long BytesRead,
    long SkippedRecords,
    ulong OrphanSize,
    ulong OrphanFiles);
