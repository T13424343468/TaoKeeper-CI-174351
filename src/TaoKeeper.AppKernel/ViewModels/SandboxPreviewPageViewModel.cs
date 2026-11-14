using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules.SandboxPreview;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>越界记录展示行（时间+目标路径+拦截原因，R-P13-4 逐条展开上报）。</summary>
public sealed record PreviewInterceptionRow(string TimeLocal, string TargetPath, string Reason);

/// <summary>
/// P13 先试后行 VM（沙箱预演）：越界 0 次如实显示 0，不造记录；未预演=未测态。
/// 三出口（留下/丢弃/转正式快照）各有真实状态流（确认→进行中→完成/失败）。
/// </summary>
public sealed class SandboxPreviewPageViewModel
{
    /// <summary>预演状态：idle=未预演（如实空态，V-A10 零预填）；running=预演中；done=已出结果。</summary>
    public string Phase { get; private set; } = "idle";

    public IReadOnlyList<PreviewInterceptionRow> Interceptions { get; private set; } = [];
    public int ViolationCount => Interceptions.Count;
    public string? LastError { get; private set; }
    public string? LastResultText { get; private set; }

    /// <summary>预演引擎由页面注入（App 层接线真实文件系统适配器；沙箱预演核心在 Core 层全测）。</summary>
    public Func<SandboxPreviewEngine>? EngineProvider { get; set; }

    /// <summary>最近一次提交/冻结的回执文本（完成/失败如实）。</summary>
    public bool HasRun => Phase == "done" || Phase == "running";

    public void BeginPreview()
    {
        Phase = "running";
        LastError = null;
        LastResultText = null;
        Interceptions = [];
    }

    /// <summary>页面在每次真实拦截回调后同步记录（引擎回调 → VM 展示，不造数据）。</summary>
    public void SyncInterceptions(SandboxPreviewEngine engine)
    {
        Interceptions = engine.Interceptions
            .Select(i => new PreviewInterceptionRow(
                TimeLocal: i.TimeUtc.ToLocalTime().ToString("HH:mm:ss"),
                TargetPath: i.TargetPath,
                Reason: i.Reason))
            .ToList();
    }

    public async System.Threading.Tasks.Task CommitAsync(SandboxPreviewEngine engine, string label)
    {
        Phase = "done";
        var r = await engine.CommitAsync(label).ConfigureAwait(false);
        LastResultText = r.Succeeded ? "预演已转正式执行，快照已先行落盘（可在时光回溯还原）" : r.Error;
        SyncInterceptions(engine);
    }

    public void Discard(SandboxPreviewEngine engine)
    {
        Phase = "done";
        var r = engine.Discard();
        LastResultText = r.Succeeded ? "预演已整体丢弃，零残留（仅留预演已丢弃日志）" : r.Error;
        SyncInterceptions(engine);
    }

    public async System.Threading.Tasks.Task FreezeAsync(SandboxPreviewEngine engine, string label)
    {
        Phase = "done";
        var r = await engine.FreezeAsSnapshotAsync(label).ConfigureAwait(false);
        LastResultText = r.Succeeded ? "已原样冻结为独立快照（含全部拦截记录），可在时光回溯查看" : r.Error;
        SyncInterceptions(engine);
    }
}
