using System;
using System.Collections.Generic;
using TaoKeeper.AppKernel.Navigation;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>
/// 体检运行状态机（R-P4-1 全链，G4 初始态基准）：Idle=未测态基线（V-A10 口径：本页无用户默认值，
/// 初始态即「尚未体检」，出自 G4 初始态基准）；Running=扫描进行中（唯一禁止重入态）；
/// Completed=本轮已有真实结果；Failed=本轮未取得结果（原因如实展示，可重跑）。
/// 状态只能经 BeginRun/CompleteRun/FailRun 改变（封装：内外有别）。
/// </summary>
public enum CheckupRunPhase { Idle, Running, Completed, Failed }

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

    /// <summary>运行状态机当前相位（初始 Idle=未测态基线，G4）。</summary>
    public CheckupRunPhase RunPhase { get; private set; } = CheckupRunPhase.Idle;

    /// <summary>最近一次失败原因（Failed 相位展示；成功/未跑为 null）。</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>相位变化回调（壳接线到界面刷新；VM 层不依赖 UI 框架）。</summary>
    public Action<CheckupRunPhase>? PhaseChanged { get; set; }

    /// <summary>
    /// R-P4-1 全链：点「开始体检」→ Running（进度态）→ 执行器产出上下文 → Completed（结果态）；
    /// 执行器抛错/取消 → Failed（原因如实）。executor=null 如实 Failed（未接线不假跑）。
    /// Running 相位拒绝重入；本方法零写动作（执行器也必须是只读扫描）。
    /// </summary>
    public async Task RunAsync(Func<CancellationToken, Task<HealthContext>>? executor, CancellationToken cancellation = default)
    {
        if (RunPhase == CheckupRunPhase.Running)
            throw new InvalidOperationException("体检进行中，不能重复启动");
        if (executor is null)
        {
            FailRun("体检通道尚未接线，本次未执行任何扫描");
            PhaseChanged?.Invoke(RunPhase);
            return;
        }

        BeginRun();
        try
        {
            var context = await executor(cancellation).ConfigureAwait(false);
            CompleteRun(context);
        }
        catch (OperationCanceledException)
        {
            FailRun("体检已取消，未做任何改动");
        }
        catch (Exception ex)
        {
            FailRun($"体检未完成：{ex.Message}（只读扫描，未动任何文件）");
        }
        PhaseChanged?.Invoke(RunPhase);
    }

    /// <summary>进入 Running 相位（仅 Idle/Completed/Failed 可入；失败原因随新跑清空）。</summary>
    public void BeginRun()
    {
        if (RunPhase == CheckupRunPhase.Running)
            throw new InvalidOperationException("体检进行中，不能重复启动");
        RunPhase = CheckupRunPhase.Running;
        FailureMessage = null;
        PhaseChanged?.Invoke(RunPhase);
    }

    /// <summary>执行器成功返回后落结果（空上下文按未测态如实呈现，RunCheckup 自判）。</summary>
    public void CompleteRun(HealthContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RunCheckup(context);
        RunPhase = CheckupRunPhase.Completed;
    }

    /// <summary>本轮未取得结果：原因如实记录，可再次 BeginRun 重跑。</summary>
    public void FailRun(string reason)
    {
        FailureMessage = reason;
        RunPhase = CheckupRunPhase.Failed;
    }

    /// <summary>发现清单（按严重度降序，与引擎输出同序）。</summary>
    public IReadOnlyList<CheckupFindingItem> Findings { get; private set; } = Array.Empty<CheckupFindingItem>();

    /// <summary>老机器建议卡（R-P4-9，G8 落点）：只建议不静默改；未探测为空清单。</summary>
    public IReadOnlyList<OldMachineAdviceCard> AdviceCards { get; private set; } = Array.Empty<OldMachineAdviceCard>();

    /// <summary>注入本机探测结果并重建老机器建议卡（说人话+实测依据；零执行动作）。</summary>
    public CheckupPageViewModel ApplyMachineProbe(MachineProbe probe)
    {
        AdviceCards = OldMachineAdviceBuilder.Build(probe);
        return this;
    }

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
