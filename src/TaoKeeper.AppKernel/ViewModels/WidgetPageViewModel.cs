using System;
using System.Collections.Generic;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>桌面挂件页/设置项视图模型（UI 框架零依赖，WinUI3 壳直接绑定）。
/// 职责：挂件开关意图转述（关=移除自启+退出零残留）、挂件一帧数据转述
/// （内存/垃圾概览/后台进程，未就绪灰显不编数、过期灰显）、一键清理确认入口转述。
/// 道系边界：本层零写动作——开关只产出 WidgetBootAction 意图交适配层执行；
/// 清理必须过明细确认浮层，回收站语义在上层执行。</summary>
public sealed class WidgetPageViewModel
{
    /// <summary>数据新鲜判定窗口（默认 90 秒；过期灰显而不是展示旧数）。</summary>
    public TimeSpan FreshWindow { get; init; } = TimeSpan.FromSeconds(90);

    private readonly CleanWidgetPolicy _policy = new();

    /// <summary>当前挂件设置（默认：开、随系统自启）。</summary>
    public WidgetSettings Settings { get; private set; } = new();

    /// <summary>最近一帧挂件数据；未就绪为 null。</summary>
    public WidgetFrame? LatestFrame { get; private set; }

    /// <summary>数据是否过期（true=灰显整帧，不展示旧数）；无数据的初始态即灰显。</summary>
    public bool DataStale { get; private set; } = true;

    /// <summary>用户点过「一键清理」后的待确认态；确认浮层走完才解除。</summary>
    public bool CleanRequestPending { get; private set; }

    /// <summary>内存占用主显文本；未就绪为空串（界面灰显）。</summary>
    public string MemoryText => LatestFrame is { } f ? $"{f.MemoryUsedPercent:F0}%" : string.Empty;

    /// <summary>垃圾概览文本；任一项未就绪如实说明，不编数。</summary>
    public string JunkText => LatestFrame is { JunkCandidateBytes: { } bytes, JunkCandidateCount: { } count } f
        ? $"垃圾候选约 {Fmt(bytes)}（{count} 项）"
        : "垃圾概览未就绪";

    /// <summary>后台进程概览文本；未就绪为空串（界面灰显）。</summary>
    public string BackgroundText => LatestFrame is { BackgroundProcessCount: { } n } ? $"后台进程 {n} 个" : string.Empty;

    /// <summary>一键清理入口的确认提示（回收站语义写明可还原）。</summary>
    public string CleanConfirmHint =>
        "清理前先过明细确认浮层：逐项可勾、全部移入回收站、可还原；确认后才执行。";

    /// <summary>合入一帧新数据并按当前时刻刷新新鲜度。</summary>
    public WidgetPageViewModel ApplyFrame(WidgetFrame frame, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(frame);
        LatestFrame = frame;
        DataStale = !frame.IsFresh(FreshWindow, nowUtc);
        return this;
    }

    /// <summary>定时器/每次刷新调用：按当前时刻重判新鲜度（不换数据只改灰显态）。</summary>
    public WidgetPageViewModel RefreshFreshness(DateTimeOffset nowUtc)
    {
        DataStale = LatestFrame is null || !LatestFrame.IsFresh(FreshWindow, nowUtc);
        return this;
    }

    /// <summary>挂件总开关：产出启动项/进程操作意图（适配层执行），设置随记。</summary>
    public WidgetBootAction SetWidgetEnabled(bool enabled, DateTimeOffset nowUtc)
    {
        Settings = Settings with { Enabled = enabled };
        DataStale = !enabled || DataStale || RefreshNeeded(nowUtc);
        CleanRequestPending = false; // 挂件关了，待确认态一并清
        return _policy.ApplyToggle(enabled);
    }

    /// <summary>挂件自启开关（挂件开着才有意义；关闭挂件时自启随总开关一并注销）。</summary>
    public WidgetBootAction SetAutostartWithSystem(bool enabled, DateTimeOffset nowUtc)
    {
        Settings = Settings with { AutostartWithSystem = enabled };
        return _policy.ApplyToggle(Settings.Enabled && enabled);
    }

    /// <summary>设置页「本产品组件」自启条目转述（原样展示给启动项页，透明呈现）。</summary>
    public StartupItemEntry StartupEntryForDisplay(bool actuallyEnabled)
        => Settings.ToStartupItemEntry(actuallyEnabled);

    /// <summary>启动项页过滤转述：全部原样展示；建议禁用剔除管家自身与疑似同类。</summary>
    public (IReadOnlyList<StartupItemEntry> Items, IReadOnlyList<StartupItemEntry> RecommendedToDisable)
        FilterStartupPage(IReadOnlyList<StartupItemEntry> allItems)
        => _policy.FilterForStartupPage(allItems);

    /// <summary>用户点挂件「一键清理」：预检（永远要求确认）并置待确认态。</summary>
    public string PrepareCleanRequest()
    {
        CleanRequestPending = true;
        return CleanConfirmHint;
    }

    /// <summary>确认浮层走完（无论清没清成）解除待确认态。</summary>
    public void CompleteCleanRequest() => CleanRequestPending = false;

    private bool RefreshNeeded(DateTimeOffset nowUtc)
        => LatestFrame is { } f && !f.IsFresh(FreshWindow, nowUtc);

    private static string Fmt(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F1} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F0} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}
