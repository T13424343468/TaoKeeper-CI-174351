using System;
using System.Collections.Generic;
using TaoKeeper.Core.Models;

namespace TaoKeeper.Core.Modules;

/// <summary>本产品自身标识：挂件自启等自有组件在启动项清单里以此归属。</summary>
public static class TaoKeeperIdentity
{
    public const string OwnProductId = "TaoKeeper";

    /// <summary>是否为本产品自有启动项。</summary>
    public static bool IsOwnComponent(StartupItemEntry item) =>
        string.Equals(item.OwningProductId, OwnProductId, StringComparison.OrdinalIgnoreCase);

    /// <summary>从启动项清单中分离自有组件与第三方项（挂件自启不进第三方加速建议，避免自己清自己）。</summary>
    public static (IReadOnlyList<StartupItemEntry> Own, IReadOnlyList<StartupItemEntry> ThirdParty)
        SplitOwn(IEnumerable<StartupItemEntry> items)
    {
        var own = new List<StartupItemEntry>();
        var third = new List<StartupItemEntry>();
        foreach (var item in items)
            (IsOwnComponent(item) ? own : third).Add(item);
        return (own, third);
    }
}

/// <summary>清理挂件设置（设置页开关，道系口径：关闭即退出、零后台残留；默认开）。</summary>
public sealed record WidgetSettings(
    bool Enabled = true,
    /// <summary>挂件常驻需要开机自启；用户关闭挂件时自启一并注销，重开即回。</summary>
    bool AutostartWithSystem = true)
{
    public const string AutostartItemId = "taokeeper-widget-autostart";

    /// <summary>对应的启动项条目（供启动项管理页呈现"本产品组件"徽标与开关）。</summary>
    public StartupItemEntry ToStartupItemEntry(bool actuallyEnabled) => new(
        AutostartItemId,
        "TaoKeeper 清理挂件",
        StartupItemSource.RegistryRun,
        "TaoKeeper",
        actuallyEnabled,
        StartupRiskLevel.Low,
        RiskHint: "本产品组件：桌面清理挂件的开机自启，可关闭",
        Command: null,
        OwningProductId: TaoKeeperIdentity.OwnProductId);
}

/// <summary>挂件一帧数据：环形内存图 + 垃圾概览 + 后台进程概览，与主窗口监控页同源。
/// 命名区分于 Models.WidgetSnapshot（展示层模型）；本类型是聚合器产出的一帧数据。</summary>
public sealed record WidgetFrame(
    DateTimeOffset CapturedUtc,
    double MemoryUsedPercent,
    long? JunkCandidateBytes,
    int? JunkCandidateCount,
    int? BackgroundProcessCount)
{
    /// <summary>数据是否新鲜可用（超过该时长的快照建议灰显而不是展示旧数）。</summary>
    public bool IsFresh(TimeSpan maxAge, DateTimeOffset nowUtc) => nowUtc - CapturedUtc <= maxAge;
}

/// <summary>
/// 清理挂件数据通路（挂件与主程序同进程取数，不新增常驻后台进程）。
/// 从各模块最新快照聚合出挂件一帧数据；任何一项未就绪时该字段为 null（挂件灰显该项，不编数）。
/// </summary>
public static class WidgetSnapshotAggregator
{
    public static WidgetFrame Build(
        DateTimeOffset nowUtc,
        MemorySnapshot? latestMemory,
        JunkScanResult? latestJunkScan,
        StartupProcessSnapshot? latestProcessScan)
    {
        double memPercent = latestMemory?.PhysicalUsedPercent ?? 0;
        long? junkBytes = latestJunkScan?.TotalBytes;
        int? junkCount = latestJunkScan?.TotalCount;
        int? background = latestProcessScan?.Processes.Count;
        return new WidgetFrame(nowUtc, memPercent, junkBytes, junkCount, background);
    }

    /// <summary>挂件"一键清理"入口的预检：清理动作必须先过明细确认浮层（回收站语义在上层执行）。</summary>
    public static bool RequiresConfirmation(WidgetFrame frame) => true;
}
