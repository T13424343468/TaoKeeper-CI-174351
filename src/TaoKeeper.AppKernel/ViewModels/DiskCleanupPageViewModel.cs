using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>磁盘清理页单行（规则级聚合：同规则候选并一行，展示态字段全部就绪）。</summary>
public sealed record DiskJunkRow(
    string RuleId,
    string Title,
    string Description,
    long SizeBytes,
    string SizeText,
    int FileCount,
    /// <summary>默认勾选（道系口径：高风险规则默认不勾，须用户手动勾选）。</summary>
    bool IsChecked,
    /// <summary>分层语义（0.2）：确定垃圾默认真删（先快照），用户文件默认回收站。</summary>
    CleanupTier Tier = CleanupTier.Garbage)
{
    /// <summary>白盒证据链一行（R-P3-2：规则 Id + 分层口径同屏可见，与 Core 唯一事实源同文案）。</summary>
    public string TierText => CleanupTierPolicy.EvidenceText(RuleId, Tier);
}

/// <summary>
/// 磁盘清理页视图模型（UI 框架零依赖）：把一次垃圾扫描（JunkScanResult）按规则聚合成
/// 原型 v19「可清项目」行（默认勾选=低/中风险，高风险默认不勾），勾选变化即时重算汇总，
/// 并经既有 CleanupQueueViewModel（含 CleanupSafetyGate 路径级预检）产出待执行清单。
/// 道系边界：本层零写动作——「清理（先进回收站）」只产出经确认闸门的清单，
/// 实际执行由 CleanupCoordinator/适配层在用户确认后进行；未扫描一律如实显示，不假数据。
/// </summary>
public sealed class DiskCleanupPageViewModel
{
    private Dictionary<string, bool> _checked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一次垃圾扫描（未扫描为 null）。</summary>
    public JunkScanResult? LastScan { get; private set; }

    /// <summary>规则聚合行（体积降序，同体积按规则 Id 稳定排序）。</summary>
    public IReadOnlyList<DiskJunkRow> Rows { get; private set; } = Array.Empty<DiskJunkRow>();

    /// <summary>可清合计（字节；未扫描 0）。</summary>
    public long TotalBytes => LastScan?.TotalBytes ?? 0;

    /// <summary>环图主显文本：未扫描「尚未扫描」，零候选「很干净，暂无可清」，否则体积分档文本。</summary>
    public string TotalText { get; private set; } = "尚未扫描";

    /// <summary>环图下副显（磁盘容量概览）：未提供容量信息时如实说明，不编 512 GB。</summary>
    public string VolumeText { get; private set; } = "磁盘容量待扫描后显示";

    /// <summary>已选规则数与合计（界面「已选 N 项，合计 X」）。</summary>
    public int SelectedRuleCount { get; private set; }
    public long SelectedBytes { get; private set; }
    public string SelectedSummaryText { get; private set; } = "已选 0 项，合计 0 B";

    /// <summary>确认队列（勾选规则的全部候选入队；路径级拒绝由闸门在源头标死）。</summary>
    public CleanupQueueViewModel? Queue { get; private set; }

    public bool HasScan => LastScan is not null;
    public bool HasCandidates => LastScan is { } s && s.Candidates.Count > 0;

    /// <summary>用户文件层确认弹窗勾选「直接删除（跳过回收站）」；勾选后删前同样自动留快照（0.2）。</summary>
    public bool DirectDeleteUserFiles { get; set; }

    /// <summary>R5 资源预算一句话（清理页白盒展示：缓存可见 / 常驻 ≤50MB / 空闲零周期写入）。</summary>
    public string BudgetText => CleanupTierPolicy.BudgetStatement;

    /// <summary>从一次垃圾扫描构建页面数据。volume 可选：本机磁盘总量/已用量（字节，真机接线后传入）。</summary>
    public DiskCleanupPageViewModel Build(JunkScanResult scan, (long TotalBytes, long UsedBytes)? volume = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        LastScan = scan;

        Rows = scan.Candidates
            .GroupBy(c => c.Rule.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var rule = g.First().Rule;
                return new DiskJunkRow(
                    rule.Id, rule.Title, rule.Description,
                    g.Sum(c => c.SizeBytes), Fmt(g.Sum(c => c.SizeBytes)), g.Count(),
                    rule.Risk != JunkRisk.High,
                    CleanupTierPolicy.TierOf(rule.Risk));
            })
            .OrderByDescending(r => r.SizeBytes)
            .ThenBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _checked = Rows.ToDictionary(r => r.RuleId, r => r.IsChecked, StringComparer.OrdinalIgnoreCase);
        TotalText = scan.Candidates.Count == 0 ? "很干净，暂无可清" : Fmt(scan.TotalBytes);
        VolumeText = volume is { } v
            ? $"磁盘共 {Fmt(v.TotalBytes)} · 已用 {Fmt(v.UsedBytes)}"
            : "磁盘容量待扫描后显示";

        RebuildQueueAndSummary();
        return this;
    }

    /// <summary>勾选/取消一条规则（未扫描或未知规则安全忽略）；勾选变化后队列与汇总即时重建。</summary>
    public void ToggleRule(string ruleId, bool on)
    {
        if (!HasScan || !_checked.ContainsKey(ruleId)) return;
        _checked[ruleId] = on;
        RebuildQueueAndSummary();
    }

    /// <summary>当前勾选规则对应的待执行清单（仅已确认条目；用户文件按 DirectDeleteUserFiles 带直接删除标记）。</summary>
    public IReadOnlyList<CleanupActionItem> BuildConfirmedActions()
    {
        if (Queue is null) return Array.Empty<CleanupActionItem>();
        return Queue.Items.Where(i => i.Confirmed)
            .Select(i => new CleanupActionItem(i.FullPath, i.SizeBytes, i.Source,
                Confirmed: true, i.RuleId, i.Tier,
                DirectDelete: i.Tier == CleanupTier.UsersFile && DirectDeleteUserFiles))
            .ToList();
    }

    private void RebuildQueueAndSummary()
    {
        if (LastScan is null)
        {
            Queue = null;
            SelectedRuleCount = 0;
            SelectedBytes = 0;
            SelectedSummaryText = "已选 0 项，合计 0 B";
            return;
        }

        var checkedIds = _checked.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = LastScan.Candidates
            .Where(c => checkedIds.Contains(c.Rule.Id))
            .Select(c => (c.FullPath, c.SizeBytes, Source: "junk", (string?)c.Rule.Id))
            .ToList();
        Queue = new CleanupQueueViewModel(items, tierResolver: p =>
            LastScan.Candidates.FirstOrDefault(c => string.Equals(c.FullPath, p, StringComparison.OrdinalIgnoreCase)) is { } c
                ? CleanupTierPolicy.TierOf(c.Rule.Risk) : CleanupTier.Garbage);
        Queue.SelectAll(); // 规则级勾选即确认粒度：勾选规则的候选逐条入确认态（受保护路径仍被闸门标死）

        SelectedRuleCount = Rows.Count(r => checkedIds.Contains(r.RuleId));
        SelectedBytes = Queue.ConfirmedBytes;
        SelectedSummaryText = $"已选 {SelectedRuleCount} 项，合计 {Fmt(SelectedBytes)}";
    }

    /// <summary>体积分档文本（页壳与测试共用同口径）。</summary>
    public static string Fmt(long bytes)
    {
        if (bytes < 0) return "0 B";
        double v = bytes;
        const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
        return bytes >= GB ? $"{v / GB:0.#} GB"
             : bytes >= MB ? $"{v / MB:0.#} MB"
             : bytes >= KB ? $"{v / KB:0.#} KB"
             : $"{bytes} B";
    }
}
