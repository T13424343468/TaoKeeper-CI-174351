using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoKeeper.Core.Modules;

/// <summary>体检发现：一条可执行的问题项。</summary>
public sealed record HealthFinding(
    string RuleId,
    string Title,
    string Detail,
    HealthSeverity Severity,
    /// <summary>建议动作标识，交上层路由到对应模块（如 jump-disk-clean）。</summary>
    string SuggestedActionId)
{
    /// <summary>该发现的扣分权重。</summary>
    public int ScorePenalty => Severity switch
    {
        HealthSeverity.Info => 0,
        HealthSeverity.Advice => 5,
        HealthSeverity.Warning => 15,
        HealthSeverity.Critical => 30,
        _ => 0
    };
}

/// <summary>严重度：提示 &lt; 建议 &lt; 警告 &lt; 严重。</summary>
public enum HealthSeverity { Info, Advice, Warning, Critical }

/// <summary>体检上下文：由各模块扫描结果汇总填充；缺失项用 null 表示"本轮未测"。</summary>
public sealed record HealthContext(
    long? JunkReclaimableBytes,
    long? DuplicateReclaimableBytes,
    int? StartupItemCount,
    double? SystemDiskFreePercent,
    /// <summary>虚拟内存设置偏离推荐方案时给出提示文本（由 M9 填充）。</summary>
    string? VirtualMemoryHint = null,
    /// <summary>存储感知是否开启（M8）。</summary>
    bool? StorageSenseEnabled = null)
{
    /// <summary>空上下文：全部未测，用于首跑兜底。</summary>
    public static HealthContext Empty => new(null, null, null, null);
}

/// <summary>体检结果：评分（0–100）与发现清单。</summary>
public sealed record HealthResult(
    int Score,
    IReadOnlyList<HealthFinding> Findings,
    DateTimeOffset CheckedAtUtc);

/// <summary>
/// 体检中心规则引擎（模块 M2，可移植逻辑层）。
/// 规则表驱动：每条规则基于上下文独立评估，产出发现；评分 = 100 − 各发现扣分，下限 0。
/// 只读：体检本身不做任何修改，处理动作由用户逐项确认后路由到对应模块。
/// </summary>
public sealed class HealthCheckEngine
{
    private readonly IReadOnlyList<IHealthRule> _rules;

    public HealthCheckEngine(IReadOnlyList<IHealthRule>? rules = null)
        => _rules = rules ?? DefaultRules();

    /// <summary>默认规则表。阈值来自 TaoKeeper 设计文档 v0.4 口径，可按真机反馈调优。</summary>
    public static IReadOnlyList<IHealthRule> DefaultRules() => new List<IHealthRule>
    {
        new JunkAccumulationRule(),
        new DuplicateClusterRule(),
        new StartupItemRule(),
        new SystemDiskSpaceRule(),
        new StorageSenseRule(),
        new VirtualMemoryHintRule(),
    };

    public HealthResult Run(HealthContext context)
    {
        var findings = _rules
            .Select(r => r.Evaluate(context))
            .Where(f => f is not null)
            .Cast<HealthFinding>()
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.ScorePenalty)
            .ToList();

        var score = Math.Max(0, 100 - findings.Sum(f => f.ScorePenalty));
        return new HealthResult(score, findings, DateTimeOffset.UtcNow);
    }
}

/// <summary>一条体检规则。</summary>
public interface IHealthRule
{
    string Id { get; }
    HealthFinding? Evaluate(HealthContext context);
}

/// <summary>垃圾堆积：可清理量超过阈值提示体检清理。</summary>
public sealed class JunkAccumulationRule : IHealthRule
{
    public string Id => "junk-accumulation";
    public HealthFinding? Evaluate(HealthContext c)
    {
        if (c.JunkReclaimableBytes is not { } b) return null;
        if (b >= 5L * 1024 * 1024 * 1024)
            return new HealthFinding(Id, "垃圾堆积", $"可清理垃圾约 {Fmt(b)}，建议清一清", HealthSeverity.Warning, "jump-disk-clean");
        if (b >= 1L * 1024 * 1024 * 1024)
            return new HealthFinding(Id, "垃圾堆积", $"可清理垃圾约 {Fmt(b)}，闲时清理可释放空间", HealthSeverity.Advice, "jump-disk-clean");
        return null;
    }
    internal static string Fmt(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):F1} GB" : $"{bytes / (double)(1L << 20):F0} MB";
}

/// <summary>重复文件簇：占位超过阈值提示去重。</summary>
public sealed class DuplicateClusterRule : IHealthRule
{
    public string Id => "duplicate-cluster";
    public HealthFinding? Evaluate(HealthContext c)
    {
        if (c.DuplicateReclaimableBytes is not { } b) return null;
        if (b < 2L * 1024 * 1024 * 1024) return null;
        return new HealthFinding(Id, "重复文件堆积",
            $"发现可释放约 {JunkAccumulationRule.Fmt(b)} 的重复文件", HealthSeverity.Advice, "jump-duplicates");
    }
}

/// <summary>启动项过多：开机负担提示。</summary>
public sealed class StartupItemRule : IHealthRule
{
    public string Id => "startup-items";
    public HealthFinding? Evaluate(HealthContext c) => c.StartupItemCount switch
    {
        null => null,
        var n when n >= 25 => new HealthFinding(Id, "启动项偏多",
            $"开机自启 {n} 项，拖慢开机速度，建议梳理", HealthSeverity.Warning, "jump-startup"),
        var n when n >= 15 => new HealthFinding(Id, "启动项较多",
            $"开机自启 {n} 项，可按需关闭不常用的", HealthSeverity.Advice, "jump-startup"),
        _ => null
    };
}

/// <summary>系统盘余量紧张：低于 15% 告警。</summary>
public sealed class SystemDiskSpaceRule : IHealthRule
{
    public string Id => "system-disk-space";
    public HealthFinding? Evaluate(HealthContext c) => c.SystemDiskFreePercent switch
    {
        null => null,
        var p when p < 10 => new HealthFinding(Id, "系统盘空间告急",
            $"系统盘仅剩 {p:F0}%，可能影响系统更新与运行", HealthSeverity.Critical, "jump-space-analysis"),
        var p when p < 15 => new HealthFinding(Id, "系统盘空间紧张",
            $"系统盘剩余 {p:F0}%，建议清理或迁移大文件", HealthSeverity.Warning, "jump-space-analysis"),
        _ => null
    };
}

/// <summary>存储感知未开启：仅提示，开启与否由用户定。</summary>
public sealed class StorageSenseRule : IHealthRule
{
    public string Id => "storage-sense";
    public HealthFinding? Evaluate(HealthContext c) => c.StorageSenseEnabled == false
        ? new HealthFinding(Id, "存储感知未开启",
            "开启后可按你设定的节奏定时自动清理", HealthSeverity.Info, "jump-storage-sense")
        : null;
}

/// <summary>虚拟内存设置提示：仅转述 M9 的核对提示，不代做判断。</summary>
public sealed class VirtualMemoryHintRule : IHealthRule
{
    public string Id => "virtual-memory-hint";
    public HealthFinding? Evaluate(HealthContext c) => string.IsNullOrWhiteSpace(c.VirtualMemoryHint)
        ? null
        : new HealthFinding(Id, "虚拟内存设置可优化", c.VirtualMemoryHint, HealthSeverity.Info, "jump-virtual-memory");
}
