using System;

namespace TaoKeeper.Core.Modules;

/// <summary>存储感知清理范围（用户逐项勾选；总开关默认关闭）。</summary>
[Flags]
public enum StorageCleanupScope
{
    None = 0,
    TempFiles = 1,
    RecycleBinOld = 2,
    DownloadsAged = 4,
    BrowserCache = 8,
}

/// <summary>存储感知计划：默认关闭；开启后按节奏定时清理，只清用户勾选的范围。</summary>
public sealed record StorageSensePlan(
    bool Enabled,
    StorageCleanupScope Scope,
    /// <summary>清理间隔（天），最小 1。</summary>
    int IntervalDays,
    /// <summary>上次运行时间；null=从未运行。</summary>
    DateTimeOffset? LastRunUtc,
    /// <summary>系统重启后未到点也补跑一次（错过窗口容忍）。</summary>
    bool RunOnStartupIfOverdue,
    /// <summary>计划启用时刻（未运行前的首跑锚点）；null=未启用过。</summary>
    DateTimeOffset? EnabledAtUtc = null)
{
    public static StorageSensePlan Default => new(false, StorageCleanupScope.None, 7, null, true);

    /// <summary>下次应运行时间；关闭时返回 null。从未运行时以启用时刻锚定一个周期。</summary>
    public DateTimeOffset? NextRunUtc(DateTimeOffset now)
        => Enabled && (LastRunUtc ?? EnabledAtUtc) is { } anchor
            ? anchor.AddDays(IntervalDays)
            : null;

    /// <summary>当前是否到点（到点或已逾期都返回 true，由适配层触发，实际执行仍走用户确认过的范围）。</summary>
    public bool IsDue(DateTimeOffset now)
        => Enabled && NextRunUtc(now) is { } next && now >= next;
}

/// <summary>一次定时清理的执行计划（执行前供确认浮层展示：明确清单+总量+回收站语义）。</summary>
public sealed record ScheduledCleanupPreview(
    DateTimeOffset ScheduledForUtc,
    StorageCleanupScope Scope,
    int CandidateCount,
    long ReclaimableBytes)
{
    /// <summary>确认文案（回收站语义写明可还原）。</summary>
    public string ConfirmText => $"即将按计划清理 {CandidateCount} 项（约 {ReclaimableBytes / 1024.0 / 1024:F0} MB），全部移入回收站，可还原。";
}

/// <summary>存储感知定时策略（模块 M8 可移植逻辑层）。
/// 道系边界：引擎只计算"到没到点、清什么"，真正的删除动作永远由用户逐次确认后走回收站语义。</summary>
public static class StorageSenseEngine
{
    /// <summary>开启并校验计划：间隔至少 1 天，范围至少一项。</summary>
    public static StorageSensePlan Enable(StorageSensePlan current, StorageCleanupScope scope, int intervalDays,
        bool runOnStartupIfOverdue, DateTimeOffset now)
    {
        if (scope == StorageCleanupScope.None)
            throw new ArgumentException("至少勾选一项清理范围", nameof(scope));
        return current with
        {
            Enabled = true,
            Scope = scope,
            IntervalDays = Math.Max(1, intervalDays),
            RunOnStartupIfOverdue = runOnStartupIfOverdue,
            LastRunUtc = current.LastRunUtc,
            EnabledAtUtc = current.EnabledAtUtc ?? now,
        };
    }

    /// <summary>到点时产出确认预览；未到点返回 null（什么也不做，不静默清理）。</summary>
    public static ScheduledCleanupPreview? Evaluate(DateTimeOffset now, StorageSensePlan plan, int candidateCount, long reclaimableBytes)
    {
        if (!plan.IsDue(now)) return null;
        return new ScheduledCleanupPreview(
            now, plan.Scope, candidateCount, reclaimableBytes);
    }
}
