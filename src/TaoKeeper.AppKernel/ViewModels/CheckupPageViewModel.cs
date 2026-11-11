using System;
using System.Collections.Generic;
using TaoKeeper.AppKernel.Navigation;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>体检页视图模型：把 HealthCheckEngine 结果整理成可直接绑定的展示态（UI 框架零依赖）。</summary>
public sealed class CheckupPageViewModel
{
    private readonly Dictionary<string, AppRoute> _jumpRoutes;

    /// <summary>体检得分（0–100）。</summary>
    public int Score { get; private set; }

    /// <summary>评分带文案（措辞对齐原型 v19 体检页；沙箱措辞，原型核对后以原型为准）。</summary>
    public string ScoreBandText { get; private set; } = string.Empty;

    /// <summary>本轮是否为「未测」态（上下文全空 = 首跑兜底，界面显示「尚未体检」而非 100 分）。</summary>
    public bool IsUntested { get; private set; }

    /// <summary>发现清单（按严重度降序，与引擎输出同序）。</summary>
    public IReadOnlyList<CheckupFindingItem> Findings { get; private set; } = Array.Empty<CheckupFindingItem>();

    /// <summary>发现项 → 目标页面路由映射（SuggestedActionId 与导航页对应，未知动作不跳转返回 null）。</summary>
    private static readonly Dictionary<string, AppRoute> DefaultJumpRoutes = new()
    {
        ["jump-disk-clean"] = AppRoute.Disk,        // 磁盘清理
        ["jump-space-analysis"] = AppRoute.Disk,    // 空间分析在磁盘清理页
        ["jump-duplicates"] = AppRoute.Tools,       // 重复文件在工具箱页
        ["jump-startup"] = AppRoute.Boost,          // 启动项在性能加速页
        ["jump-storage-sense"] = AppRoute.Storage,  // 存储感知页
        ["jump-virtual-memory"] = AppRoute.Mem,     // 虚拟内存归内存管理页
    };

    public CheckupPageViewModel(Dictionary<string, AppRoute>? jumpRoutes = null)
        => _jumpRoutes = jumpRoutes ?? DefaultJumpRoutes;

    /// <summary>跑一次体检并建立展示态；上下文由各模块扫描结果汇总填充。</summary>
    public CheckupPageViewModel RunCheckup(HealthContext context, DateTimeOffset? atUtc = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        IsUntested = context == HealthContext.Empty ||
            context.JunkReclaimableBytes is null && context.DuplicateReclaimableBytes is null &&
            context.StartupItemCount is null && context.SystemDiskFreePercent is null &&
            context.StorageSenseEnabled is null && context.VirtualMemoryHint is null;

        var engine = new HealthCheckEngine();
        var result = engine.Run(context);
        Score = IsUntested ? 0 : result.Score; // 未测态不展示 100 分假象，评分置 0 由界面以「未测」呈现
        Findings = result.Findings.Select(f => new CheckupFindingItem(
            f.RuleId, f.Title, f.Detail, f.Severity,
            _jumpRoutes.TryGetValue(f.SuggestedActionId, out var route) ? route : null,
            f.SuggestedActionId)).ToList();
        ScoreBandText = BandText(result.Score, IsUntested);
        return this;
    }

    /// <summary>发现严重度 → 主题色值（朱砂=问题、金=建议、墨灰=提示），随主题即时取值。</summary>
    public string AccentColorOf(HealthSeverity severity, ThemeTokens tokens) => severity switch
    {
        HealthSeverity.Critical => tokens.Cinnabar,
        HealthSeverity.Warning => tokens.Cinnabar,
        HealthSeverity.Advice => tokens.Gold,
        _ => tokens.Ink2,
    };

    /// <summary>发现严重度 → 底衬色值（浅洗色，供卡片底）。</summary>
    public string WashColorOf(HealthSeverity severity, ThemeTokens tokens) => severity switch
    {
        HealthSeverity.Critical => tokens.CinnabarWash,
        HealthSeverity.Warning => tokens.CinnabarWash,
        HealthSeverity.Advice => tokens.BambooWash,
        _ => tokens.AzureWash,
    };

    private static string BandText(int score, bool untested) => untested ? "尚未体检"
        : score >= 90 ? "状态很好"
        : score >= 75 ? "总体良好"
        : score >= 60 ? "有待优化"
        : "建议全面清理";
}

/// <summary>体检页单条发现（绑定就绪：含主题色键与跳转路由）。</summary>
public sealed record CheckupFindingItem(
    string RuleId,
    string Title,
    string Detail,
    HealthSeverity Severity,
    /// <summary>点击跳转的目标页；null=无对应页面（展示但不跳）。</summary>
    AppRoute? JumpRoute,
    /// <summary>原始动作标识（诊断与埋点用）。</summary>
    string SuggestedActionId);
