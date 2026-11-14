using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules.IncidentReplay;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>时间线行（本地时区展示；来源四路：安装/设置/崩溃/性能）。</summary>
public sealed record IncidentTimelineRow(string TimeLocal, string SourceLabel, string Summary, string? Detail);

/// <summary>
/// P15 事发回放 VM（归因报告）：时间窗 72h 默认、24h/7d 可选（R-P15-2）；
/// 空窗显式提示（不出空白列表）；归因三件套=最可能原因+验证步骤+置信度；
/// 零外发（R-P15-3/N8）：VM 层零网络引用，输入全部来自注入的本机事件流。
/// </summary>
public sealed class IncidentReplayPageViewModel
{
    public int WindowHours { get; private set; } = IncidentReplayEngine.DefaultWindowHours;
    public bool IsEmptyWindow { get; private set; }
    public string EmptyWindowText => IncidentReplayEngine.EmptyWindowMessage;
    public IReadOnlyList<IncidentTimelineRow> Timeline { get; private set; } = [];
    public string? MostLikelyCause { get; private set; }
    public IReadOnlyList<string> VerificationSteps { get; private set; } = [];
    public double Confidence { get; private set; }
    public bool HasAttribution { get; private set; }

    /// <summary>事件流由页面/组合根注入（本机快照链事务+事件日志读数，零外发）。</summary>
    public void Load(IEnumerable<IncidentEvent> events, DateTimeOffset symptomTimeUtc, int? windowHours = null, string? symptomKeyword = null)
    {
        if (windowHours is int w) WindowHours = w;
        var report = IncidentReplayEngine.Replay(events, symptomTimeUtc, WindowHours, symptomKeyword);
        IsEmptyWindow = report.IsEmptyWindow;
        Timeline = report.Timeline.Select(e => new IncidentTimelineRow(
            TimeLocal: e.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm"),
            SourceLabel: e.Source switch
            {
                "install" => "安装",
                "settings" => "设置",
                "crash" => "崩溃",
                "perf" => "性能",
                _ => e.Source,
            },
            Summary: e.Summary,
            Detail: e.Detail)).ToList();
        HasAttribution = report.Attribution is not null;
        if (report.Attribution is { } a)
        {
            MostLikelyCause = a.MostLikelyCause;
            VerificationSteps = a.VerificationSteps;
            Confidence = a.Confidence;
        }
        else
        {
            MostLikelyCause = null;
            VerificationSteps = [];
            Confidence = 0;
        }
    }

    /// <summary>切换时间窗换数据（R-P15-2）。</summary>
    public void SetWindow(IEnumerable<IncidentEvent> events, DateTimeOffset symptomTimeUtc, int windowHours, string? symptomKeyword = null) =>
        Load(events, symptomTimeUtc, windowHours, symptomKeyword);
}
