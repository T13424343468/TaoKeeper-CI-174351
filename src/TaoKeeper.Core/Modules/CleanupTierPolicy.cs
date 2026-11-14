using System;

namespace TaoKeeper.Core.Modules;

/// <summary>清理分层语义（0.2 分层拍板，2026-11-14 14:3x）：
/// Garbage=确定垃圾类（缓存/临时文件/日志/缩略图等可重建缓存），默认直接真删；
/// UsersFile=用户文件（可能误删、需用户斟酌），默认进回收站，确认弹窗可勾选直接删除。</summary>
public enum CleanupTier { Garbage, UsersFile }

/// <summary>
/// 清理分层策略（唯一事实源，页面文案与编排判定同源）：
/// 铁律——真删必先留快照（删前自动拍快照、期内可还原），空间按真删量实际增加；
/// 用户文件默认回收站，勾选「直接删除」后删前同样自动留快照。
/// </summary>
public static class CleanupTierPolicy
{
    /// <summary>确定垃圾类判定：低/中风险规则（可重建缓存类）=确定垃圾；高风险规则（如安装包残留）=用户文件层。</summary>
    public static CleanupTier TierOf(JunkRisk risk) => risk == JunkRisk.High ? CleanupTier.UsersFile : CleanupTier.Garbage;

    /// <summary>分层语义展示文案（清理页白盒化：分类口径对用户可见，一步到位不藏术语）。</summary>
    public static string TierText(CleanupTier tier) => tier switch
    {
        CleanupTier.Garbage => "确定垃圾 · 默认真删（删前自动留快照，期内可还原）",
        _ => "用户文件 · 默认回收站（可还原；可勾选直接删除，删前同样留快照）",
    };

    /// <summary>白盒证据链一行文案（R-P3-2：凭什么删我——RuleId + 分层口径同屏可见）。</summary>
    public static string EvidenceText(string ruleId, CleanupTier tier) => $"规则 {ruleId} · {TierText(tier)}";

    // ── R5 资源预算口径（清理页白盒展示，全部固定口径可断言） ──

    /// <summary>常驻内存预算硬约束 ≤50MB（低配档位在其内再收，G8）。</summary>
    public const long ResidentMemoryBudgetBytes = 50L * 1024 * 1024;

    /// <summary>清理页资源预算一句话（缓存可见 / 常驻 ≤50MB / 空闲零周期写入）。</summary>
    public const string BudgetStatement = "缓存分类可见 · 常驻内存 ≤ 50 MB · 空闲零周期写入";

    /// <summary>分层语义补充说明（确认弹窗用，逐字对齐 0.2）。</summary>
    public const string BudgetDetail =
        "确定垃圾类默认真删（删前留快照、空间实际增加）；用户文件默认进回收站，可勾选直接删除（删前同样留快照）。";
}
