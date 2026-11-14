namespace TaoKeeper.Core.Modules;

/// <summary>
/// 快照链统一策略（R-P16-1 溯源；修订记录第 8 条 2026-11-14 拍板）：
/// 单快照上限 500MB 为固定口径（非设置项）；总量滚动上限 5GB 为设置项可配；
/// 粉碎类快照独立保留期默认 7 天为设置项可配。到期物理清除前二次确认（页面职责）。
/// </summary>
public static class SnapshotPolicy
{
    /// <summary>单快照硬上限 500MB（固定口径，不进设置页）。</summary>
    public const long SingleSnapshotMaxBytes = 500L * 1024 * 1024;

    /// <summary>快照总量滚动上限默认 5GB（R-P16-1；AppSettings.SnapshotQuotaBytes 可配）。</summary>
    public const long DefaultQuotaBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>粉碎类快照独立保留期默认 7 天（R-P10-4a / R-P16-1；AppSettings 可配）。</summary>
    public const int DefaultShredRetentionDays = 7;

    /// <summary>超出单快照上限时拒绝拍快照（调用方中止动作，契约第七节错误语义）。</summary>
    public const string OversizeRefusalMessage = "快照超过 500MB 单快照上限，已拒绝拍快照，危险动作未执行。";

    /// <summary>总量滚动判定：先淘汰最旧已完成事务，直到总量含新快照后仍 ≤ 配额；未完结事务不触碰（引擎口径）。</summary>
    public static bool NeedsEviction(long currentTotalBytes, long incomingBytes, long quotaBytes) =>
        currentTotalBytes + incomingBytes > quotaBytes;
}
