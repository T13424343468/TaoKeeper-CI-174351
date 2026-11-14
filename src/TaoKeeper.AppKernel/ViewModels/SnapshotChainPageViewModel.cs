using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules.SnapshotChain;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>快照清单行：时间本地时区展示（引擎 UTC 100ns → 界面本地时区，原型 spec 口径）。</summary>
public sealed record SnapshotRow(
    string TransactionId,
    string TimeLocal,
    string ActionLabel,
    int ObjectCount,
    string SizeLabel,
    bool IsShred);

/// <summary>
/// P16 时光回溯 VM（快照链）：总占用可见可清（A16-1 双入口）、还原确认流全状态
/// （确认→进度→完成/失败，A16-2）、粉碎类快照统一清点（定谳稿第五节 5）。
/// 未扫描=未测态零预填（V-A10，原型示例数禁入）。
/// </summary>
public sealed class SnapshotChainPageViewModel
{
    private readonly SnapshotQuotaTracker _tracker;

    public SnapshotChainPageViewModel(SnapshotQuotaTracker tracker)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    }

    public long QuotaBytes { get; set; } = TaoKeeper.Core.Modules.SnapshotPolicy.DefaultQuotaBytes;
    public int ShredRetentionDays { get; set; } = TaoKeeper.Core.Modules.SnapshotPolicy.DefaultShredRetentionDays;

    public string TotalUsageText => FormatBytes(_tracker.TotalBytes);
    public string QuotaText => $"{FormatBytes(QuotaBytes)} / 总量 {FormatBytes(QuotaBytes)} 滚动上限（设置可调）";

    public IReadOnlyList<SnapshotRow> Snapshots { get; private set; } = [];

    /// <summary>刷新清单（P16 页可见可还原：清单由配额管理器统一清点，含粉碎类）。</summary>
    public void Refresh()
    {
        Snapshots = _tracker.Ledger()
            .OrderByDescending(e => e.Timestamp.SequenceNumber)
            .Select(e => new SnapshotRow(
                TransactionId: e.TransactionId,
                TimeLocal: DateTimeOffset.FromFileTime(e.Timestamp.FileTimeUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ActionLabel: e.ActionType.StartsWith("shred", StringComparison.OrdinalIgnoreCase) ? "粉碎快照" : e.ActionType,
                ObjectCount: e.ObjectCount,
                SizeLabel: FormatBytes(e.SizeBytes),
                IsShred: e.IsShred))
            .ToList();
    }

    /// <summary>粉碎类快照到期（独立保留期，物理清除前由页面二次确认）。</summary>
    public IReadOnlyList<SnapshotRow> ExpiredShredSnapshots(DateTimeOffset nowUtc) =>
        Snapshots.Where(s => s.IsShred && _tracker.Ledger()
            .Any(l => l.TransactionId == s.TransactionId
                && SnapshotQuotaTracker.IsShredExpired(
                    DateTimeOffset.FromFileTime(l.Timestamp.FileTimeUtc),
                    ShredRetentionDays, nowUtc))).ToList();

    private static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):F1} KB",
        _ => $"{b} B",
    };
}
