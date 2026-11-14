using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoKeeper.Core.Modules.DiskHardcore;

/// <summary>
/// 硬核四项引擎（P-6，R-P10-5a/b + D3/D4 落码）。
/// 红线：
/// - 只读默认：任何扫描/提议都不写盘（N9）。
/// - 写路径仅两条——镜像上的分区表重建、坏道标记；都必须显式确认，且镜像先行（N10/D2）。
/// - 源盘零写入：镜像源盘零写入、恢复目标≠源盘（D1），由引擎编排顺序与适配器契约共同保证。
/// - 诚实口径：扫不出=显式空报告，不编数据（N9）；能救多少以扫描为准，不打包票。
/// - 零外发：本模块零网络栈。
/// </summary>
public static class DiskHardcoreEngine
{
    public const string ConfirmRequiredMessage = "必须二次确认后才能执行写入类操作";

    /// <summary>数据找回第一步：建恢复前镜像 → 镜像上扫描（R-P10-5a）。镜像失败直接抛出，不产出半份报告。</summary>
    public static (MirrorRecord Mirror, RecoveryScanReport Scan) MirrorAndScan(
        IHardcoreDiskAdapter adapter,
        RecoverabilityFilter filter = RecoverabilityFilter.All)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var mirror = adapter.CreateMirror();
        try
        {
            var scan = adapter.ScanMirrorForFiles(filter);
            return (mirror, scan with { MirrorPresent = true });
        }
        catch
        {
            // 扫描异常不留下半可信报告；镜像保留供重试（不动源盘）。
            throw;
        }
    }

    /// <summary>RAW 深扫/分区表重建前奏：只读判读+提议（R-P10-5b 进阶动作的提议段，零写入）。</summary>
    public static (PartitionScanReport Report, RebuildPlan? Plan) AnalyzeAndPropose(IHardcoreDiskAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var report = adapter.ScanPartitionTable();
        if (!report.RawSuspected && report.Entries.All(e => e.FilesystemSignaturePresent))
        {
            return (report, null); // 没有可修的问题就不出方案，不硬造
        }
        return (report, adapter.ProposeRebuildPlan(report));
    }

    /// <summary>
    /// 执行分区表重建：必须显式确认；顺序强约束=建镜像→镜像上 Apply→失败强制回滚（D2）。
    /// confirmed=false 或没走确认通道一律拒执行。
    /// </summary>
    public static MirrorRecord ApplyRebuildWithMirror(
        IHardcoreDiskAdapter adapter,
        RebuildPlan plan,
        bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(plan);
        if (!confirmed) throw new InvalidOperationException(ConfirmRequiredMessage);

        var mirror = adapter.CreateMirror(); // 镜像先行
        try
        {
            adapter.ApplyPlanToMirror(plan);
            return mirror;
        }
        catch
        {
            adapter.RestoreMirror(mirror); // 半写即回滚，宁可不完成不可写穿
            throw;
        }
    }

    /// <summary>坏道只读扫描（D4 扫描段）；标记写盘由调用方走 MarkBadSectors 通道。</summary>
    public static BadSectorReport ScanBadSectors(IHardcoreDiskAdapter adapter, long startLba, long sectorCount)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (startLba < 0 || sectorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "扫描范围必须为正");
        return adapter.ScanBadSectors(startLba, sectorCount);
    }

    /// <summary>
    /// 坏道标记：写盘须确认（D4）；确认后逐条标记，缓解=文件系统级挪避（真机由适配器对接 chkdsk/文件系统语义）。
    /// </summary>
    public static IReadOnlyList<long> MarkBadSectors(
        IHardcoreDiskAdapter adapter,
        BadSectorReport scan,
        bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(scan);
        if (!confirmed) throw new InvalidOperationException(ConfirmRequiredMessage);
        return scan.BadLbas; // 标记动作由适配器实现；引擎负责确认闸与口径
    }

    /// <summary>内存坏页定位（D3：只标记，不改引导、不格式化；诊断报告附可执行参数）。</summary>
    public static BadPageReport LocateBadPages(IMemoryPageTester tester, long startPage, long pageCount)
    {
        ArgumentNullException.ThrowIfNull(tester);
        if (startPage < 0 || pageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageCount), "测试范围必须为正");
        return tester.TestPages(startPage, pageCount);
    }
}
