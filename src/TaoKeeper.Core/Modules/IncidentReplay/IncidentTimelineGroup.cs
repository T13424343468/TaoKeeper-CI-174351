namespace TaoKeeper.Core.Modules.IncidentReplay;

/// <summary>按粒度分组后的时间线组（R-P15-4：按天/周查看，跨度=快照保留上限）。</summary>
public sealed record IncidentTimelineGroup(string Label, DateTimeOffset PeriodStartUtc, IReadOnlyList<IncidentEvent> Events);
