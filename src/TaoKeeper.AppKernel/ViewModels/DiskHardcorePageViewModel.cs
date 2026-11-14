using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules.DiskHardcore;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>硬核四项行（本地展示；四路：镜像扫描/分区判读/坏页定位/坏道扫描）。</summary>
public sealed record DiskHardcoreRow(string ToolLabel, string Summary, string? Detail);

/// <summary>
/// P-6 硬核四项 VM（R-P10-5a/b + D3/D4）。
/// 只读默认：扫描/提议零写入；写路径（镜像上重建、坏道标记）必须显式确认，引擎侧强制镜像先行。
/// 诚实口径 N9：扫不出=显式空态提示，不编数据；零外发：VM 层零网络引用。
/// </summary>
public sealed class DiskHardcorePageViewModel
{
    public bool IsScanned { get; private set; }
    public string EmptyText => "尚未扫描：点「建镜像并扫描」后在本机镜像上出结果（源盘零写入）。";
    public string NoResultText => "没有扫到可恢复的文件（以实扫为准，不打包票）。";

    public bool MirrorPresent { get; private set; }
    public string? MirrorId { get; private set; }
    public bool RawSuspected { get; private set; }
    public string? RebuildSuggestion { get; private set; }
    public bool HasRebuildPlan { get; private set; }
    public IReadOnlyList<DiskHardcoreRow> Rows { get; private set; } = [];
    public IReadOnlyList<string> RecoverableFiles { get; private set; } = [];

    private RebuildPlan? _plan;
    private IHardcoreDiskAdapter? _adapter;
    private IMemoryPageTester? _memoryTester;
    private MirrorRecord? _mirror;

    /// <summary>建恢复前镜像并扫描（R-P10-5a）；随后做只读分区判读与坏页/坏道只读扫描。</summary>
    public void MirrorAndScan(IHardcoreDiskAdapter adapter, IMemoryPageTester? memoryTester = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
        _memoryTester = memoryTester;

        var (mirror, scan) = DiskHardcoreEngine.MirrorAndScan(adapter);
        _mirror = mirror;
        MirrorPresent = true;
        MirrorId = mirror.MirrorId;

        var (report, plan) = DiskHardcoreEngine.AnalyzeAndPropose(adapter);
        RawSuspected = report.RawSuspected;
        RebuildSuggestion = report.Suggestion;
        _plan = plan;
        HasRebuildPlan = plan is not null;

        var rows = new List<DiskHardcoreRow>
        {
            new("恢复前镜像", $"镜像已建（{mirror.SectorsCovered} 扇区，源盘零写入）", mirror.MirrorId),
            new("分区表判读", report.RawSuspected ? "检测到 RAW 疑似分区（文件系统签名缺失）" : "分区表正常，无需重建",
                plan is null ? null : "已生成重建方案（仅提议；执行需二次确认，镜像上操作）"),
        };

        if (scan.Files.Count == 0)
        {
            rows.Add(new DiskHardcoreRow("文件扫描", NoResultText, null));
        }
        else
        {
            rows.Add(new DiskHardcoreRow("文件扫描", $"扫到 {scan.Files.Count} 个可恢复条目", null));
        }

        if (memoryTester is not null)
        {
            var pages = DiskHardcoreEngine.LocateBadPages(memoryTester, 0, 0);
            rows.Add(new DiskHardcoreRow("内存坏页", pages.BadPageNumbers.Count == 0
                    ? $"测试 {pages.PagesChecked} 页，未发现坏页（只标记，不改引导、不格式化）"
                    : $"发现 {pages.BadPageNumbers.Count} 个坏页（只标记，不改引导、不格式化）",
                pages.ExecutableSuggestion));
        }

        var sectors = DiskHardcoreEngine.ScanBadSectors(adapter, 0, 1);
        rows.Add(new DiskHardcoreRow("坏道扫描", sectors.BadLbas.Count == 0
                ? $"检查 {sectors.SectorsChecked} 扇区，未发现坏道（标记写盘须确认）"
                : $"发现 {sectors.BadLbas.Count} 个坏道（标记写盘须确认）", null));

        Rows = rows;
        RecoverableFiles = scan.Files.Select(FormatFile).ToList();
        IsScanned = true;
    }

    /// <summary>执行重建（镜像上；二次确认后引擎强制镜像先行+失败回滚）。返回 true=已执行。</summary>
    public bool ApplyRebuild()
    {
        if (_adapter is null || _plan is null || !HasRebuildPlan) return false;
        // 页面确认对话框已在前置交互完成；本方法只接收确认后的执行请求。
        _mirror = DiskHardcoreEngine.ApplyRebuildWithMirror(_adapter, _plan, confirmed: true);
        RebuildSuggestion = "重建已在镜像上执行（源盘未动）；回滚可用快照链或镜像恢复。";
        HasRebuildPlan = false;
        return true;
    }

    /// <summary>坏道标记（写盘须确认；页面确认对话框前置）。</summary>
    public bool MarkBadSectors()
    {
        if (_adapter is null) return false;
        var scan = DiskHardcoreEngine.ScanBadSectors(_adapter, 0, 1);
        DiskHardcoreEngine.MarkBadSectors(_adapter, scan, confirmed: true);
        return true;
    }

    private static string FormatFile(RecoveredFileEntry f)
    {
        var size = f.SizeBytes switch
        {
            >= 1024 * 1024 * 1024 => $"{f.SizeBytes / 1024.0 / 1024 / 1024:F1} GB",
            >= 1024 * 1024 => $"{f.SizeBytes / 1024.0 / 1024:F1} MB",
            >= 1024 => $"{f.SizeBytes / 1024.0:F0} KB",
            _ => $"{f.SizeBytes} B",
        };
        var level = f.Recoverability switch
        {
            Recoverability.Intact => "完好可救",
            Recoverability.PartiallyOverwritten => "部分覆盖",
            Recoverability.Overwritten => "已覆盖不可救",
            _ => "未知",
        };
        var time = f.ModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "时间未知";
        var path = string.IsNullOrEmpty(f.Path) ? "" : $"｜{f.Path}";
        return $"{f.Name}｜{size}｜{time}｜{level}{path}";
    }
}
