using System;
using System.Collections.Generic;

namespace TaoKeeper.Core.Modules.DiskHardcore;

/// <summary>分区表扫描结果（R-P10-5：只读；RAW 疑似=文件系统签名缺失，仅判读不写盘）。</summary>
public sealed record PartitionScanReport(
    bool RawSuspected,
    IReadOnlyList<PartitionEntry> Entries,
    string? Suggestion);

/// <summary>单个分区条目（只读视图）。</summary>
public sealed record PartitionEntry(
    int Index,
    long StartLba,
    long SizeLba,
    string Type,
    bool FilesystemSignaturePresent);

/// <summary>重建方案（仅提议；Apply 必须显式确认+镜像先行，N9/N10）。</summary>
public sealed record RebuildPlan(
    IReadOnlyList<PartitionEntry> ProposedEntries,
    string Basis);

/// <summary>恢复前镜像回执：镜像 ID+覆盖范围（R-P10-5a 镜像源盘零写入；目标≠源盘由适配器保证并校验）。</summary>
public sealed record MirrorRecord(
    string MirrorId,
    DateTimeOffset TakenUtc,
    long SectorsCovered);

/// <summary>坏道扫描报告：只读扫描结果；标记写盘须确认（D4）。</summary>
public sealed record BadSectorReport(
    long SectorsChecked,
    IReadOnlyList<long> BadLbas);

/// <summary>内存坏页报告：只标记不格式化、不改引导（D3）；诊断报告给可执行参数。</summary>
public sealed record BadPageReport(
    long PagesChecked,
    IReadOnlyList<long> BadPageNumbers,
    string? ExecutableSuggestion);

/// <summary>可恢复度分级（R-P10-5a：完好可救/部分覆盖/已覆盖不可救）。</summary>
public enum Recoverability
{
    Intact,
    PartiallyOverwritten,
    Overwritten,
}

/// <summary>扫描出的可恢复文件条目（逐项列文件名/大小/时间/路径，诚实口径 N9）。</summary>
public sealed record RecoveredFileEntry(
    string Name,
    long SizeBytes,
    DateTimeOffset? ModifiedUtc,
    string? Path,
    Recoverability Recoverability);

/// <summary>数据找回扫描报告：扫不出=空列表+显式提示（N9 不打包票、不编数据）。</summary>
public sealed record RecoveryScanReport(
    bool MirrorPresent,
    IReadOnlyList<RecoveredFileEntry> Files);

/// <summary>硬核四项磁盘适配器（真机=扇区级 P/Invoke；沙箱=假盘注入）。所有方法默认只读，写路径仅 ApplyPlan/标记两处且引擎强制确认+镜像。</summary>
public interface IHardcoreDiskAdapter
{
    /// <summary>只读：扫描分区表并判读 RAW 疑似。</summary>
    PartitionScanReport ScanPartitionTable();

    /// <summary>只读：基于扫描报告提议重建方案，不落盘。</summary>
    RebuildPlan ProposeRebuildPlan(PartitionScanReport report);

    /// <summary>建恢复前镜像（源盘零写入；返回回执）。失败须抛异常，引擎不会继续。</summary>
    MirrorRecord CreateMirror();

    /// <summary>在镜像上执行重建（真机=写到镜像；绝不直写源盘）。</summary>
    void ApplyPlanToMirror(RebuildPlan plan);

    /// <summary>镜像回滚（Apply 失败时引擎强制调用，保证不半写）。</summary>
    void RestoreMirror(MirrorRecord mirror);

    /// <summary>只读：扇区坏道扫描（chkdsk 只读复扫+SMART 一致性由适配器组合保证，D4）。</summary>
    BadSectorReport ScanBadSectors(long startLba, long sectorCount);

    /// <summary>在镜像上执行恢复前镜像导出的扇区级深扫（RAW 深扫），产出可恢复文件清单。</summary>
    RecoveryScanReport ScanMirrorForFiles(RecoverabilityFilter filter);
}

/// <summary>可恢复度过滤（缺省=全列）。</summary>
public enum RecoverabilityFilter { All, IntactOnly }

/// <summary>内存页测试适配器（D3：只标记；不改引导、不格式化）。</summary>
public interface IMemoryPageTester
{
    BadPageReport TestPages(long startPage, long pageCount);
}
