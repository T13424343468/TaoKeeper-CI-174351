using System;
using System.Collections.Generic;

namespace TaoKeeper.Core.Models;

/// <summary>一键清理的待确认明细（点"一键清理"先过这层确认浮层，绝不直接执行）。</summary>
public sealed record WidgetCleanPreview(
    IReadOnlyList<string> ItemSummaries,
    long TotalReclaimableBytes,
    /// <summary>确认后进入执行；执行结果走回收站语义，可还原。</summary>
    string ConfirmText)
{
    public static WidgetCleanPreview Create(int junkCount, long junkBytes, int processCount)
    {
        var items = new List<string>();
        if (junkCount > 0) items.Add($"垃圾文件 {junkCount} 项");
        if (processCount > 0) items.Add($"后台进程 {processCount} 个（逐个列出，安全结束）");
        return new WidgetCleanPreview(
            items,
            junkBytes,
            items.Count == 0
                ? "当前没有可清理的项目。"
                : $"{string.Join("、", items)}，合计约 {junkBytes / 1024.0 / 1024:F0} MB，全部移入回收站，可还原。");
    }
}

/// <summary>挂件一键清理的执行回执（完成后向用户报数）。</summary>
public sealed record WidgetCleanReceipt(
    DateTimeOffset FinishedUtc,
    int CleanedItems,
    long ReclaimedBytes,
    /// <summary>回收站路径或还原入口说明。</summary>
    string UndoHint);
