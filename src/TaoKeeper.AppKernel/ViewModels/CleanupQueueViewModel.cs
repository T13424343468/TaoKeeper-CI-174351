using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>
/// 清理确认队列视图模型（清理各页共用的确认层，UI 框架零依赖，WinUI3 壳直接绑定）。
/// 预检与执行共用 Core 的 CleanupSafetyGate（同一事实源，口径不漂移）：
/// 受保护路径/盘符根的条目从源头就不可勾选，根本进不了执行清单。
/// 本模型零写动作——BuildActions 只产出待执行清单，动不动作由 CleanupCoordinator 决定。
/// </summary>
public sealed class CleanupQueueViewModel
{
    private readonly ISet<string> _protectedNames;

    /// <summary>队列条目（只读；勾选状态经条目自身事件上抛）。</summary>
    public IReadOnlyList<CleanupQueueItem> Items { get; }

    /// <summary>当前已确认条数。</summary>
    public int ConfirmedCount => Items.Count(i => i.Confirmed);

    /// <summary>已确认条目的合计体积（字节），界面展示「将释放 X」。</summary>
    public long ConfirmedBytes => Items.Where(i => i.Confirmed).Sum(i => i.SizeBytes);

    /// <summary>任一条目状态变化时触发一次（由条目事件统一上抛，方法内不重复触发）。</summary>
    public event Action? QueueChanged;

    public CleanupQueueViewModel(
        IEnumerable<(string FullPath, long SizeBytes, string Source, string? RuleId)> items,
        IEnumerable<string>? protectedNames = null)
    {
        _protectedNames = protectedNames as ISet<string>
            ?? new HashSet<string>(protectedNames ?? CleanupSafetyGate.DefaultProtectedNames,
                StringComparer.OrdinalIgnoreCase);

        Items = items.Select(a => new CleanupQueueItem(a.FullPath, a.SizeBytes, a.Source, a.RuleId,
            // 入队即预检：路径级拒绝在源头标死，不可勾选
            CleanupSafetyGate.Evaluate(a.FullPath, confirmed: true, _protectedNames)))
            .ToList();
        foreach (var item in Items)
            item.ConfirmedChanged += () => QueueChanged?.Invoke();
    }

    /// <summary>全选（仅可确认项；受保护/盘符根条目永远不可勾）。</summary>
    public void SelectAll()
    {
        foreach (var item in Items.Where(i => i.CanConfirm)) item.Confirmed = true;
    }

    /// <summary>全不选（受保护条目状态本来就不可能为真）。</summary>
    public void ClearAll()
    {
        foreach (var item in Items) item.Confirmed = false;
    }

    /// <summary>按路径勾选/取消；返回 false=该条目不可确认（路径级拒绝）。</summary>
    public bool Toggle(string fullPath)
    {
        var item = Items.FirstOrDefault(i => string.Equals(i.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (item is null || !item.CanConfirm) return false;
        item.Confirmed = !item.Confirmed;
        return true;
    }

    /// <summary>产出待执行清单：仅已确认条目，Confirmed 恒为 true（编排层再兜底一次闸门）。</summary>
    public IReadOnlyList<CleanupActionItem> BuildActions() =>
        Items.Where(i => i.Confirmed)
             .Select(i => new CleanupActionItem(i.FullPath, i.SizeBytes, i.Source,
                 Confirmed: true, i.RuleId))
             .ToList();
}

/// <summary>确认队列的单条目。</summary>
public sealed class CleanupQueueItem
{
    /// <summary>路径级拒绝理由（非 null 表示这条永远不可确认执行）。</summary>
    public string? PathRefusal { get; }

    /// <summary>可否勾选确认（受保护/盘符根条目恒 false）。</summary>
    public bool CanConfirm => PathRefusal is null;

    public string FullPath { get; }
    public long SizeBytes { get; }
    public string Source { get; }
    public string? RuleId { get; }

    private bool _confirmed;
    /// <summary>用户逐条确认；不可确认条目赋 true 会被拒（零静默删除）。</summary>
    public bool Confirmed
    {
        get => _confirmed;
        set
        {
            if (!CanConfirm && value) return; // 拒绝项勾不上，也不报异常打断界面
            if (_confirmed == value) return;
            _confirmed = value;
            ConfirmedChanged?.Invoke();
        }
    }

    internal event Action? ConfirmedChanged;

    public CleanupQueueItem(string fullPath, long sizeBytes, string source, string? ruleId, string? pathRefusal)
    {
        FullPath = fullPath;
        SizeBytes = sizeBytes;
        Source = source;
        RuleId = ruleId;
        PathRefusal = pathRefusal;
    }
}
