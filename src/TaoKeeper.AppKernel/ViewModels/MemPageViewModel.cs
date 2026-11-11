using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>内存压力档位（85% 告警线联动，随设置可调）。</summary>
public enum MemoryPressureLevel
{
    Normal = 0,     // 低于告警线
    Alert,          // 达到告警线
    Critical,       // 达到紧急线（默认 95%）
}

/// <summary>
/// 内存管理页视图模型（UI 框架零依赖，WinUI3 壳直接绑定）：
/// 快照 → 压力档位与展示文案、趋势绘图序列（环形序列降采样）、占用排行、
/// 页面文件预设建议与校验转述。本模型零写动作——应用页面文件设置属于
/// 用户确认后的适配层动作，本层只把结果整理给界面。
/// </summary>
public sealed class MemPageViewModel
{
    /// <summary>物理内存告警线（百分比，来自设置页，默认 85；0=用户已关告警）。</summary>
    public int AlertPercent { get; }

    /// <summary>紧急线：告警线 + 10，上限 100。</summary>
    public int CriticalPercent { get; }

    /// <summary>占用排行最多展示条数。</summary>
    public int TopCount { get; init; } = 15;

    public MemPageViewModel(int alertPercent = 85)
    {
        AlertPercent = Math.Clamp(alertPercent, 0, 100);
        CriticalPercent = Math.Min(100, AlertPercent + 10);
    }

    /// <summary>当前压力档位。</summary>
    public MemoryPressureLevel PressureLevel { get; private set; } = MemoryPressureLevel.Normal;

    /// <summary>占用率文本（界面主显；未测态为空串）。</summary>
    public string UsageText { get; private set; } = string.Empty;

    /// <summary>明细文本（物理已用/总量与页面文件用量）。</summary>
    public string DetailText { get; private set; } = string.Empty;

    /// <summary>趋势绘图序列（降采样后的快照列表）。</summary>
    public IReadOnlyList<MemorySnapshot> Trend { get; private set; } = Array.Empty<MemorySnapshot>();

    /// <summary>占用排行（按工作集降序，最多 TopCount 条）。</summary>
    public IReadOnlyList<MemoryTopEntry> TopProcesses { get; private set; } = Array.Empty<MemoryTopEntry>();

    private readonly MemoryTrendSeries _series = new();

    /// <summary>合入一份新快照并重算展示态。</summary>
    public MemPageViewModel ApplySnapshot(MemorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _series.Append(snapshot);

        var usedPercent = snapshot.PhysicalUsedPercent;
        PressureLevel = AlertPercent == 0 ? MemoryPressureLevel.Normal
            : usedPercent >= CriticalPercent ? MemoryPressureLevel.Critical
            : usedPercent >= AlertPercent ? MemoryPressureLevel.Alert
            : MemoryPressureLevel.Normal;

        UsageText = $"{usedPercent:F0}%";
        DetailText = $"物理内存 {Fmt(snapshot.TotalPhysicalBytes - snapshot.AvailablePhysicalBytes)} / {Fmt(snapshot.TotalPhysicalBytes)}"
            + (snapshot.PageFileUsedPercent is { } pf ? $"，页面文件 {pf:F0}%" : string.Empty);
        Trend = _series.Downsample(2);
        return this;
    }

    /// <summary>合入占用排行（按工作集降序截取；快照序不动）。</summary>
    public MemPageViewModel ApplyTopProcesses(IEnumerable<MemoryTopEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        TopProcesses = entries
            .OrderByDescending(e => e.WorkingSetBytes)
            .Take(Math.Max(0, TopCount))
            .ToList();
        return this;
    }

    /// <summary>页面文件预设建议值转述（自定义页展示；null=系统托管/无需数值）。</summary>
    public static (int InitialMb, int MaximumMb)? SuggestPageFile(PageFileSettings settings, long physicalMemoryBytes) =>
        settings.SuggestedValues(physicalMemoryBytes / (1024 * 1024));

    /// <summary>页面文件校验转述：错误阻止应用、警告提示可继续，文案原样给界面。</summary>
    public static PageFileValidation ValidatePageFile(PageFileSettings settings, long physicalMemoryBytes) =>
        PageFileSettingsValidator.Validate(settings, physicalMemoryBytes);

    /// <summary>压力档位 → 主题色（正常=青瓷、告警=金、紧急=朱砂），随主题即时取值。</summary>
    public string PressureColorOf(MemoryPressureLevel level, ThemeTokens tokens) => level switch
    {
        MemoryPressureLevel.Critical => tokens.Cinnabar,
        MemoryPressureLevel.Alert => tokens.Gold,
        _ => tokens.Bamboo,
    };

    private static string Fmt(long bytes)
    {
        double v = bytes;
        const long GB = 1024L * 1024 * 1024;
        return bytes >= GB ? $"{v / GB:F1} GB" : $"{v / (1024.0 * 1024):F0} MB";
    }
}
