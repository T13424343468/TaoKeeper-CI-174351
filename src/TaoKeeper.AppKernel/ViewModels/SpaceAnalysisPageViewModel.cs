using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>空间分析页单个目录条目（展示态：路径已重建，体积已转述）。</summary>
public sealed record SpaceAnalysisItem(
    ulong RecordNo,
    string PathText,        // 完整路径；父链重建失败时退回裸名并置 PathFallback
    bool PathFallback,
    long RecursiveSize,
    string SizeText,
    long FileCount);

/// <summary>
/// 空间分析页视图模型（UI 框架零依赖、零写动作）：主路径接 MftStreamingScanner 的 MftStreamScanResult，
/// 目录聚合按体积降序取 Top-N，路径经 MftPathBuilder 重建（失败退裸名并如实标记，不编路径）；
/// 汇总含坏记录数、读取量与孤儿体积（父链缺失/成环部分，与聚合口径一致），孤儿不入 Top-N 清单。
/// 原生库不可用时经 BuildFromPortableScan 接便携目录遍历产出（PortableMode 如实标记，MFT 专属字段留空/零）。
/// </summary>
public sealed class SpaceAnalysisPageViewModel
{
    /// <summary>最近一次 MFT 流式扫描结果（便携模式或未扫描时为 null）。</summary>
    public MftStreamScanResult? LastScan { get; private set; }

    /// <summary>是否处于便携降级模式（原生库不可用，走目录遍历路径，无坏记录/孤儿/记录号语义）。</summary>
    public bool PortableMode { get; private set; }

    /// <summary>Top-N 目录条目（体积降序，同体积按记录号升序；便携模式按路径升序）。</summary>
    public IReadOnlyList<SpaceAnalysisItem> TopDirectories { get; private set; }
        = Array.Empty<SpaceAnalysisItem>();

    /// <summary>未进 Top-N 的目录数（界面「其余 N 项」用）。</summary>
    public int HiddenDirectoryCount { get; private set; }

    /// <summary>汇总文本（扫描后刷新）。</summary>
    public string SummaryText { get; private set; } = "尚未扫描";

    /// <summary>是否有可展示条目（空卷/无聚合时界面显示空态）。</summary>
    public bool HasItems => TopDirectories.Count > 0;

    /// <summary>孤儿体积（父链缺失或成环部分，不参与目录树，仅如实入汇总；便携模式恒 0）。</summary>
    public ulong OrphanSize { get; private set; }

    /// <summary>孤儿文件数（便携模式恒 0）。</summary>
    public ulong OrphanFiles { get; private set; }

    /// <summary>从流式扫描结果构建页面数据。topN 为 Top-N 容量（默认 20，非正抛错）。</summary>
    public SpaceAnalysisPageViewModel BuildFromStream(MftStreamScanResult result, int topN = 20)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (topN <= 0) throw new ArgumentOutOfRangeException(nameof(topN));
        PortableMode = false;
        LastScan = result;
        OrphanSize = result.OrphanSize;
        OrphanFiles = result.OrphanFiles;

        var pathBuilder = new MftPathBuilder(result.Entries);
        var paths = pathBuilder.BuildAll();

        var ordered = result.DirAggregates
            .OrderByDescending(a => a.RecursiveSize)
            .ThenBy(a => a.RecordNo)
            .ToList();
        HiddenDirectoryCount = Math.Max(0, ordered.Count - topN);
        TopDirectories = ordered.Take(topN)
            .Select(a => paths.FullPaths.TryGetValue(a.RecordNo, out var path)
                ? new SpaceAnalysisItem(a.RecordNo, path, PathFallback: false,
                    unchecked((long)a.RecursiveSize), Fmt(unchecked((long)a.RecursiveSize)), unchecked((long)a.FileCount))
                : new SpaceAnalysisItem(a.RecordNo, NameOf(result, a.RecordNo), PathFallback: true,
                    unchecked((long)a.RecursiveSize), Fmt(unchecked((long)a.RecursiveSize)), unchecked((long)a.FileCount)))
            .ToList();

        var orphanNote = result.OrphanSize > 0
            ? $"，未归档 {Fmt((long)result.OrphanSize)}（{result.OrphanFiles} 项）"
            : "";
        SummaryText = $"目录 {ordered.Count} 项，记录 {result.Entries.Count} 条，"
            + $"坏记录 {result.SkippedRecords} 条，已读 {Fmt(result.BytesRead)}{orphanNote}";
        return this;
    }

    /// <summary>
    /// 便携降级构建（原生库不可用，TryScan 返 null 时走此路）：接 PortableDiskScanner 的目录遍历产出。
    /// 无坏记录/孤儿/记录号语义，RecordNo 恒 0；排序同体积按路径升序保证稳定展示。
    /// </summary>
    public SpaceAnalysisPageViewModel BuildFromPortableScan(ScanResult scan, int topN = 20)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (topN <= 0) throw new ArgumentOutOfRangeException(nameof(topN));
        PortableMode = true;
        LastScan = null;
        OrphanSize = 0;
        OrphanFiles = 0;

        var ordered = scan.Directories
            .OrderByDescending(d => d.TotalBytes)
            .ThenBy(d => d.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        HiddenDirectoryCount = Math.Max(0, ordered.Count - topN);
        TopDirectories = ordered.Take(topN)
            .Select(d => new SpaceAnalysisItem(0, d.FullPath, PathFallback: false,
                d.TotalBytes, Fmt(d.TotalBytes), d.FileCount))
            .ToList();

        SummaryText = $"便携模式：目录 {ordered.Count} 项，文件 {scan.TotalFiles} 个，合计 {Fmt(scan.TotalBytes)}";
        return this;
    }

    /// <summary>体积分层色：超大（≥1GB）朱砂、中（≥100MB）金、其余竹青，随主题即时取值。</summary>
    public string TierColorOf(long bytes, ThemeTokens tokens) => bytes switch
    {
        >= 1024L * 1024 * 1024 => tokens.Cinnabar,
        >= 100L * 1024 * 1024 => tokens.Gold,
        _ => tokens.Bamboo,
    };

    private static string NameOf(MftStreamScanResult result, ulong recordNo)
    {
        foreach (var e in result.Entries)
            if (e.RecordNo == recordNo)
                return e.Name;
        return $"记录 {recordNo}";
    }

    private static string Fmt(long bytes)
    {
        double v = bytes;
        const long GB = 1024L * 1024 * 1024, MB = 1024 * 1024, KB = 1024;
        return bytes >= GB ? $"{v / GB:F1} GB"
             : bytes >= MB ? $"{v / MB:F0} MB"
             : bytes >= KB ? $"{v / KB:F0} KB"
             : $"{bytes} B";
    }
}
