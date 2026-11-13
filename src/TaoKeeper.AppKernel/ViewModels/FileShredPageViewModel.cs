using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>
/// 粉碎运行状态机（R-P10-4a~4b，对齐 CheckupRunPhase 家法）：Idle=未选未碎态；
/// Running=粉碎进行中（唯一禁止重入态）；Completed=本轮已有真实回执；
/// Failed=本轮未完成（取消/失败原因如实展示，可重跑）。状态只能经 RunAsync/FailRun 改变。
/// </summary>
public enum FileShredRunPhase { Idle, Running, Completed, Failed }

/// <summary>
/// 文件粉碎页视图模型：计划汇总文案 + 粉碎执行状态机（UI 框架零依赖）。
/// 红线 N1 由引擎收口（快照未落盘绝不永久删除），本 VM 只如实转述结果，不做进度欺骗。
/// </summary>
public sealed class FileShredPageViewModel
{
    /// <summary>运行状态机当前相位（初始 Idle）。</summary>
    public FileShredRunPhase RunPhase { get; private set; } = FileShredRunPhase.Idle;

    /// <summary>最近一次失败原因（Failed 相位展示；成功/未跑为 null）。</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>当前计划汇总文案（「N 项可粉碎，合计 X」；未选=如实空态）。</summary>
    public string PlanSummaryText { get; private set; } = "尚未选择文件";

    /// <summary>最近一次回执：已粉碎个数（0=本轮没有成功粉碎项）。</summary>
    public int ShreddedCount { get; private set; }

    /// <summary>最近一次回执：释放空间（引擎实测合计，字节）。</summary>
    public long FreedBytes { get; private set; }

    /// <summary>相位变化回调（壳接线到界面刷新；VM 层不依赖 UI 框架）。</summary>
    public Action<FileShredRunPhase>? PhaseChanged { get; set; }

    /// <summary>
    /// 计划汇总文案（R-P10-4a 确认前置）：可粉碎项计数与合计体积；不可选项计数如实带出（不静默丢）。
    /// </summary>
    public static string BuildPlanSummary(IReadOnlyList<ShredItem> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Count == 0) return "尚未选择文件";
        var allowed = plan.Where(i => i.Allowed).ToList();
        var denied = plan.Count - allowed.Count;
        var total = allowed.Sum(i => i.SizeBytes);
        var head = allowed.Count == 0
            ? "没有可粉碎的文件"
            : $"{allowed.Count} 项可粉碎，合计 {DiskCleanupPageViewModel.Fmt(total)}";
        return denied == 0 ? head : $"{head}；{denied} 项不可选（见逐项说明）";
    }

    /// <summary>选择文件后落计划汇总（本方法零写动作，粉碎须经页面确认后由 RunAsync 执行）。</summary>
    public void BeginPlan(IReadOnlyList<ShredItem> plan)
    {
        PlanSummaryText = BuildPlanSummary(plan);
        PhaseChanged?.Invoke(RunPhase);
    }

    /// <summary>
    /// 执行粉碎（R-P10-4b）：Running（进度态）→ 执行器产出回执 → Completed（个数+释放空间如实）；
    /// 取消/抛错 → Failed（原因如实：已碎保持已碎、未处理原状）。executor=null 如实 Failed（未接线不假碎）。
    /// Running 相位拒绝重入。
    /// </summary>
    public async Task RunAsync(Func<CancellationToken, Task<ShredRunResult>>? executor, CancellationToken cancellation = default)
    {
        if (RunPhase == FileShredRunPhase.Running)
            throw new InvalidOperationException("粉碎进行中，不能重复启动");
        if (executor is null)
        {
            FailRun("粉碎通道尚未接线，本次未执行任何粉碎");
            PhaseChanged?.Invoke(RunPhase);
            return;
        }

        RunPhase = FileShredRunPhase.Running;
        FailureMessage = null;
        PhaseChanged?.Invoke(RunPhase);
        try
        {
            var result = await executor(cancellation).ConfigureAwait(false);
            ShreddedCount = result.ShreddedCount;
            FreedBytes = result.FreedBytes;
            RunPhase = FileShredRunPhase.Completed;
        }
        catch (OperationCanceledException)
        {
            FailRun("粉碎已取消：已碎的保持已碎（7 天内可从快照还原），未处理的保持原状");
        }
        catch (Exception ex)
        {
            FailRun($"粉碎未完成：{ex.Message}");
        }
        PhaseChanged?.Invoke(RunPhase);
    }

    /// <summary>本轮未完成：原因如实记录，可再次 RunAsync 重跑。</summary>
    public void FailRun(string reason)
    {
        FailureMessage = reason;
        RunPhase = FileShredRunPhase.Failed;
    }

    /// <summary>回执文案（R-P10-4b：个数+释放空间，与引擎实测一致）。</summary>
    public string ReceiptText => RunPhase == FileShredRunPhase.Completed
        ? $"已粉碎 {ShreddedCount} 个文件，释放 {DiskCleanupPageViewModel.Fmt(FreedBytes)}；7 天内可从快照还原。"
        : string.Empty;
}
