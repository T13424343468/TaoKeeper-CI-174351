using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>工具箱页签内清单归属。</summary>
public enum ToolboxSection
{
    LargeFiles = 0,    // 大文件
    EmptyFolders,      // 空文件夹
}

/// <summary>
/// 工具箱页视图模型（UI 框架零依赖）：把一次只读扫描的 ToolboxPlanner 计划
/// （大文件 Top-N + 空文件夹）转成统一的清理确认队列（CleanupQueueViewModel），
/// 逐条确认闸门与执行编排复用同一套 Core 契约。本模型零写动作。
/// </summary>
public sealed class ToolboxPageViewModel
{
    /// <summary>最近一次规划结果（null=尚未规划）。</summary>
    public ToolboxPlan? Plan { get; private set; }

    /// <summary>确认队列（大文件与空文件夹合并入队；路径级拒绝由闸门在源头标死）。</summary>
    public CleanupQueueViewModel? Queue { get; private set; }

    /// <summary>概览文本：大文件合计与空文件夹数（规划后刷新）。</summary>
    public string SummaryText { get; private set; } = "尚未扫描";

    /// <summary>大文件区是否为空（界面隐藏空区用）。</summary>
    public bool HasLargeFiles => Plan is { } p && p.LargeFiles.Count > 0;

    /// <summary>空文件夹区是否为空。</summary>
    public bool HasEmptyFolders => Plan is { } p && p.EmptyFolders.Count > 0;

    /// <summary>队列条目归属（供界面分区展示；未入队条目不含）。</summary>
    public IReadOnlyDictionary<string, ToolboxSection> Sections { get; private set; }
        = new Dictionary<string, ToolboxSection>();

    /// <summary>
    /// 从一次扫描结果规划并建队。items 组合大文件与空文件夹建议项：
    /// 空文件夹体积按 0 记（删目录不释放文件体积，释放来自后续文件删除）。
    /// </summary>
    public ToolboxPageViewModel BuildFromScan(ScanResult scan, ToolboxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        Plan = ToolboxPlanner.BuildPlan(scan, options);

        var items = new List<(string FullPath, long SizeBytes, string Source, string? RuleId)>();
        var sections = new Dictionary<string, ToolboxSection>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in Plan.LargeFiles)
        {
            items.Add((f.FullPath, f.SizeBytes, "toolbox", "large-file"));
            sections[f.FullPath] = ToolboxSection.LargeFiles;
        }
        foreach (var e in Plan.EmptyFolders)
        {
            items.Add((e.FullPath, 0, "toolbox", "empty-folder"));
            sections[e.FullPath] = ToolboxSection.EmptyFolders;
        }

        Queue = new CleanupQueueViewModel(items);
        Sections = sections;

        SummaryText = $"大文件 {Plan.LargeFiles.Count} 项（合计 {Fmt(Plan.LargeFilesTotalBytes)}），空文件夹 {Plan.EmptyFolders.Count} 个";
        return this;
    }

    /// <summary>规划是否给出建议（两者皆空时界面显示「很干净，无需处理」）。</summary>
    public bool HasSuggestions => Plan is { } p && (p.LargeFiles.Count > 0 || p.EmptyFolders.Count > 0);

    /// <summary>清单条目 → 主题色（大文件=金、空文件夹=青瓷，随主题即时取值）。</summary>
    public string SectionColorOf(ToolboxSection section, ThemeTokens tokens) => section switch
    {
        ToolboxSection.LargeFiles => tokens.Gold,
        _ => tokens.Azure,
    };

    private static string Fmt(long bytes)
    {
        double v = bytes;
        const long GB = 1024L * 1024 * 1024, MB = 1024 * 1024;
        return bytes >= GB ? $"{v / GB:F1} GB"
             : bytes >= MB ? $"{v / MB:F0} MB"
             : $"{bytes} B";
    }
}
