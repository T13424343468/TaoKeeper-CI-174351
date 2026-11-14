namespace TaoKeeper.Core.Modules.IncidentReplay;

/// <summary>四路事件流条目（R-P15-1：安装记录/设置变更/崩溃蓝屏日志/性能指标）。</summary>
public sealed record IncidentEvent(
    string Source,           // install / settings / crash / perf
    DateTimeOffset TimeUtc,
    string Summary,
    string? Detail = null);

/// <summary>归因结论：最可能原因 + 验证步骤（逐条可照做）+ 置信度（C2 引擎口径 0~1）。</summary>
public sealed record AttributionResult(
    string MostLikelyCause,
    IReadOnlyList<string> VerificationSteps,
    double Confidence);

/// <summary>事发回放报告：时间窗 + 锚点前后事件时间线 + 归因三件套。</summary>
public sealed record IncidentReplayReport(
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    bool IsEmptyWindow,
    IReadOnlyList<IncidentEvent> Timeline,
    AttributionResult? Attribution);

/// <summary>
/// 事发回放引擎（P15，R-P15-1~4 落码）。
/// 红线 R-P15-3 / N8：归因全程本地 diff 快照驱动，零数据外发——本模块零网络栈，输入全部来自注入的本机事件流。
/// 时间窗 72h 默认（R-P15-2），24h/7d 可选；空窗显式提示（不出空白列表）。
/// 跨度=快照保留上限（默认 5GB 滚动口径），无任意时刻回放（可验收写法 4，宣传与实现同口径防夸大）。
/// </summary>
public static class IncidentReplayEngine
{
    public static readonly int[] SupportedWindowsHours = [24, 72, 168];
    public const int DefaultWindowHours = 72;
    public const string EmptyWindowMessage = "本时间窗内没有记录到事件（空窗显式提示，不是漏记）";

    /// <summary>按时间窗聚合四路事件流，对症状时间锚点输出时间线与归因。</summary>
    public static IncidentReplayReport Replay(
        IEnumerable<IncidentEvent> events,
        DateTimeOffset symptomTimeUtc,
        int windowHours = DefaultWindowHours,
        string? symptomKeyword = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (!SupportedWindowsHours.Contains(windowHours)) windowHours = DefaultWindowHours;
        var start = symptomTimeUtc.AddHours(-windowHours);
        var inWindow = events
            .Where(e => e.TimeUtc >= start && e.TimeUtc <= symptomTimeUtc)
            .OrderBy(e => e.TimeUtc)
            .ToList();
        // 边界取闭区间两端：windowHours=24 时恰在 -24h 整点的事件也算窗内（切换换数据口径）

        if (inWindow.Count == 0)
        {
            return new IncidentReplayReport(start, symptomTimeUtc, IsEmptyWindow: true, [], null);
        }

        // 归因（C2 引擎口径，本地 diff）：按窗口内事件与症状关键词的相关度计分；关键词缺省时按事件类型权重（crash > settings > install > perf）。
        var scored = inWindow
            .GroupBy(e => e.Source)
            .Select(g =>
            {
                var (cause, steps, weight) = DescribeSource(g.Key, g.Count());
                var keywordBoost = symptomKeyword is not null && g.Any(e =>
                    e.Summary.Contains(symptomKeyword, StringComparison.OrdinalIgnoreCase) ||
                    (e.Detail?.Contains(symptomKeyword, StringComparison.OrdinalIgnoreCase) ?? false))
                    ? 0.25 : 0.0;
                return (Result: new AttributionResult(cause, steps, Math.Min(1.0, weight + keywordBoost)), LastAt: g.Max(e => e.TimeUtc));
            })
            .OrderByDescending(x => x.Result.Confidence)
            .ThenByDescending(x => x.LastAt)
            .ToList();

        var best = scored[0].Result;
        return new IncidentReplayReport(start, symptomTimeUtc, IsEmptyWindow: false, inWindow, best);
    }

    /// <summary>按天/周粒度分组时间线（R-P15-4）；粒度非法回退按天，输出按时间倒序（最近在前）。</summary>
    public static IReadOnlyList<IncidentTimelineGroup> GroupByGranularity(
        IEnumerable<IncidentEvent> events, string granularity)
    {
        ArgumentNullException.ThrowIfNull(events);
        var span = granularity.Equals("week", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
        var groups = events
            .GroupBy(e => e.TimeUtc.Ticks / span.Ticks)
            .Select(g => new IncidentTimelineGroup(
                Label: span.TotalDays == 7
                    ? $"{new DateTimeOffset(g.Key * span.Ticks, TimeSpan.Zero):yyyy-MM-dd} 起一周"
                    : $"{new DateTimeOffset(g.Key * span.Ticks, TimeSpan.Zero):yyyy-MM-dd}",
                PeriodStartUtc: new DateTimeOffset(g.Key * span.Ticks, TimeSpan.Zero),
                Events: g.OrderByDescending(e => e.TimeUtc).ToList()))
            .OrderByDescending(g => g.PeriodStartUtc)
            .ToList();
        return groups;
    }

    private static (string Cause, string[] Steps, double Weight) DescribeSource(string source, int count) => source switch
    {
        "install" => ($"时间窗内有 {count} 条安装记录，最可能原因：新装软件引入的变更",
            ["打开「软件管家 → 安装账」，逐条对照本时间窗内安装项", "对最近一条安装项尝试卸载后观察症状是否消失", "确认卸载后仍异常则恢复安装并反馈"], 0.45),
        "settings" => ($"时间窗内有 {count} 条设置变更，最可能原因：设置变更引发的副作用",
            ["打开「设置」，对照本时间窗内改动过的开关逐项回退", "回退最可疑一项后观察症状", "确认与设置无关再改回"], 0.40),
        "crash" => ($"时间窗内有 {count} 条崩溃/蓝屏记录，最可能原因：系统异常事件",
            ["打开本页时间线，查看崩溃时间点前后 5 分钟内的事件", "记录错误码并对照微软官方错误码表", "连续复现时导出归因报告联系反馈"], 0.60),
        "perf" => ($"时间窗内有 {count} 条性能指标记录，最可能原因：资源占用异常",
            ["打开「性能加速」查看同期内存/磁盘读数", "对照本页时间线找出占用突增时间点", "重启后复测确认"], 0.30),
        _ => ($"时间窗内有 {count} 条 {source} 记录，最可能原因：待归因", ["查看时间线逐条核对"], 0.10),
    };
}
