using System;
using System.Linq;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>存储感知页视图模型（UI 框架零依赖，WinUI3 壳直接绑定）。
/// 职责：计划开关与范围逐项勾选的转述、间隔设置、到点预览与确认文案转述。
/// 道系边界：本层零写动作——不删任何文件；到点只产出确认预览，
/// 实际清理永远等用户逐次确认后走回收站语义（适配层执行）。</summary>
public sealed class StorageSensePageViewModel
{
    /// <summary>四个清理范围的中文展示名。</summary>
    private static readonly (StorageCleanupScope Flag, string Name)[] ScopeNames =
    {
        (StorageCleanupScope.TempFiles, "临时文件"),
        (StorageCleanupScope.RecycleBinOld, "回收站过期项"),
        (StorageCleanupScope.DownloadsAged, "下载目录滞留件"),
        (StorageCleanupScope.BrowserCache, "浏览器缓存"),
    };

    /// <summary>当前计划（默认：关、无范围、7 天）。</summary>
    public StorageSensePlan Plan { get; private set; } = StorageSensePlan.Default;

    /// <summary>到点后待确认的清理预览；用户确认或到点解除前一直保留。</summary>
    public ScheduledCleanupPreview? PendingPreview { get; private set; }

    /// <summary>页面状态文案（主显）。now 用于推算下次运行时间。</summary>
    public string GetStatusText(DateTimeOffset now)
    {
        if (!Plan.Enabled)
            return "存储感知已关闭（默认）。开启后也只在你逐次确认时才清理。";
        var next = PendingPreview is { } p
            ? $"到点待确认：{p.ConfirmText}"
            : Plan.NextRunUtc(now) is { } n ? $"下次 {n.ToLocalTime():yyyy-MM-dd HH:mm}" : "已开启，尚未排期";
        return $"存储感知已开启：每 {Plan.IntervalDays} 天一次，清[{ScopeDisplay}]，{next}";
    }

    /// <summary>当前勾选范围的中文清单（空=未勾选）。</summary>
    public string ScopeDisplay => string.Join("、",
        ScopeNames.Where(s => (Plan.Scope & s.Flag) != 0).Select(s => s.Name));

    /// <summary>清理范围目录（界面逐项渲染用；与 ScopeNames 同源同序，只含名称不含描述）。</summary>
    public static IReadOnlyList<(StorageCleanupScope Flag, string Name)> ScopeCatalog => ScopeNames;

    /// <summary>逐项勾选/取消一个范围。开启状态下清空最后一项报红（先关总开关）。</summary>
    public void ToggleScope(StorageCleanupScope scope, bool on)
    {
        var next = on ? Plan.Scope | scope : Plan.Scope & ~scope;
        if (Plan.Enabled && next == StorageCleanupScope.None)
            throw new InvalidOperationException("至少保留一项清理范围；要全停请先关闭存储感知。");
        Plan = Plan with { Scope = next };
    }

    /// <summary>设置清理间隔（天），最小 1。</summary>
    public void SetIntervalDays(int days)
        => Plan = Plan with { IntervalDays = Math.Max(1, days) };

    /// <summary>开启存储感知（范围至少一项，由引擎校验报红）。</summary>
    public void Enable(bool runOnStartupIfOverdue, DateTimeOffset now)
        => Plan = StorageSenseEngine.Enable(Plan, Plan.Scope, Plan.IntervalDays, runOnStartupIfOverdue, now);

    /// <summary>关闭存储感知（清除到点待确认状态；已勾选范围保留，便于下次再开）。</summary>
    public void Disable()
    {
        Plan = Plan with { Enabled = false };
        PendingPreview = null;
    }

    /// <summary>检查到点并产出确认预览；未到点返回 false 且不做任何事。</summary>
    public bool EvaluateDue(DateTimeOffset now, int candidateCount, long reclaimableBytes)
    {
        var preview = StorageSenseEngine.Evaluate(now, Plan, candidateCount, reclaimableBytes);
        if (preview is null)
            return false;
        PendingPreview = preview;
        return true;
    }

    /// <summary>用户确认后由适配层执行，执行完成回填本方法记账（改写上次运行锚点）。</summary>
    public void MarkRunCompleted(DateTimeOffset completedUtc)
    {
        Plan = Plan with { LastRunUtc = completedUtc };
        PendingPreview = null;
    }
}
