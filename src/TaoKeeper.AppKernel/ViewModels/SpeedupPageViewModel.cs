using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>一键加速页视图模型（UI 框架零依赖，WinUI3 壳直接绑定）。
/// 职责：编排转述——把 SpeedupCoordinator 的计划（建议+依据）整理给界面逐条勾选，
/// 用户显式勾选提交后才把确认清单交适配层执行；执行回执逐条转述。
/// 道系边界：零写动作默认态——计划只列不执行；勾选即确认、未勾绝不进执行清单。</summary>
public sealed class SpeedupPageViewModel
{
    private readonly SpeedupCoordinator _coordinator;

    /// <summary>进程快照提供者（宿主运行期接线；未接线=null → 进程排行如实空置，不假数据）。</summary>
    public Func<StartupProcessSnapshot?>? ProcessSnapshotProvider { get; set; }

    /// <summary>注入加速编排（适配层由外部装配；默认内存台架仅测试用）。</summary>
    public SpeedupPageViewModel(SpeedupCoordinator? coordinator = null)
        => _coordinator = coordinator ?? new SpeedupCoordinator(new InMemoryStartupAdapter());

    /// <summary>最近一次计划；未刷新为 null（界面显示未测态）。</summary>
    public SpeedupPlan? Plan { get; private set; }

    /// <summary>执行回执（最近一次，按提交顺序）。</summary>
    public IReadOnlyList<SpeedupApplyResult> LastResults { get; private set; } = Array.Empty<SpeedupApplyResult>();

    /// <summary>页面摘要行（未刷新为「尚未体检加速」）。</summary>
    public string SummaryText => Plan?.SummaryText ?? "尚未体检加速";

    /// <summary>建议禁用清单（高风险，理由已带）。</summary>
    public IReadOnlyList<SpeedupSuggestion> DisableSuggestions => Plan?.DisableSuggestions ?? Array.Empty<SpeedupSuggestion>();

    /// <summary>自行判断清单（中风险）。</summary>
    public IReadOnlyList<SpeedupSuggestion> ReviewSuggestions => Plan?.ReviewSuggestions ?? Array.Empty<SpeedupSuggestion>();

    /// <summary>内存瘦身建议（含依据；false=本次无建议）。</summary>
    public bool MemoryTrimSuggested => Plan?.MemoryTrimSuggested ?? false;

    /// <summary>刷新加速计划（只列建议，绝不执行）。</summary>
    public async Task<SpeedupPageViewModel> RefreshPlanAsync(MemorySnapshot? memory = null,
        SpeedupPolicy? policy = null, CancellationToken cancellation = default)
    {
        Plan = await _coordinator.PlanSpeedupAsync(policy, memory, cancellation).ConfigureAwait(false);
        return this;
    }

    /// <summary>
    /// 进程占用排行（直接消费 Core 既有 StartupProcessSnapshot.Processes 契约：
    /// 按工作集字节降序取前 10；快照未接线为空 → 界面如实空置，不假数据）。
    /// </summary>
    public IReadOnlyList<ProcessEntry> ProcessRanking
    {
        get
        {
            var processes = ProcessSnapshotProvider?.Invoke()?.Processes
                ?? (IReadOnlyList<ProcessEntry>)Array.Empty<ProcessEntry>();
            return processes
                .OrderByDescending(static p => p.WorkingSetBytes)
                .Take(10)
                .ToArray();
        }
    }

    /// <summary>进程排行一行的转述文案（界面直用）：名称 · PID · 内存 · CPU · 发布者（缺失如实省略）。</summary>
    public static string Describe(ProcessEntry process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var text = $"{process.ProcessName} · PID {process.ProcessId} · {DescribeBytes(process.WorkingSetBytes)}"
            + $" · CPU {process.CpuPercent:0.0}%";
        return process.Publisher is { } publisher ? $"{text} · {publisher}" : text;
    }

    /// <summary>字节量的中文转述（MiB 口径，两个小数位内）。</summary>
    private static string DescribeBytes(long bytes)
    {
        const long MiB = 1024L * 1024;
        return bytes >= MiB ? $"{(double)bytes / MiB:0.##} MiB" : $"{(double)bytes / 1024:0.##} KiB";
    }

    /// <summary>提交用户显式勾选的确认清单并执行（未勾的绝不进入；空勾选直接返回空回执）。</summary>
    public async Task<IReadOnlyList<SpeedupApplyResult>> ApplyConfirmedAsync(
        IEnumerable<string> confirmedItemIds, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedItemIds);
        var confirmed = confirmedItemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .Select(id => (ItemId: id, UserConfirmed: true))
            .ToList();
        if (confirmed.Count == 0)
        {
            LastResults = Array.Empty<SpeedupApplyResult>();
            return LastResults;
        }
        LastResults = await _coordinator.ApplyDisableAsync(confirmed, cancellation).ConfigureAwait(false);
        return LastResults;
    }

    /// <summary>一条建议的列表展示文案（名称 + 依据）。</summary>
    public static string Describe(SpeedupSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        return $"{suggestion.Item.Name}：{suggestion.Reason}";
    }

    /// <summary>回执汇总文案（界面执行后展示）。</summary>
    public string ResultsSummaryText
    {
        get
        {
            if (LastResults.Count == 0)
                return "本次未执行任何加速动作";
            int ok = LastResults.Count(r => r.Success);
            return $"处理 {LastResults.Count} 项：成功 {ok}、未成 {LastResults.Count - ok}；逐条结果见明细";
        }
    }
}
