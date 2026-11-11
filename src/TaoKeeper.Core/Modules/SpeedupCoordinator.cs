using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Models;

namespace TaoKeeper.Core.Modules;

/// <summary>
/// 启动项控制适配器契约（性能加速模块的可移植边界）。
/// Windows 真机由注册表 Run 键/启动文件夹/计划任务适配层实现；沙箱用内存假适配器测编排。
/// 红线：禁用/启用只能逐项、经用户确认后执行；适配层绝不批量静默改系统。
/// </summary>
public interface IStartupControlAdapter
{
    /// <summary>列出开机启动项（只读清单，含风险分级）。</summary>
    Task<IReadOnlyList<StartupItemEntry>> ListStartupItemsAsync(CancellationToken cancellation = default);

    /// <summary>执行单项启用/禁用；只处理传入的这一个条目，不做任何连带动作。</summary>
    Task<StartupToggleResult> ToggleStartupAsync(string itemId, bool enable, CancellationToken cancellation = default);
}

/// <summary>内存假适配器：性能加速编排的沙箱测试，不碰真实系统。</summary>
public sealed class InMemoryStartupAdapter : IStartupControlAdapter
{
    private readonly List<StartupItemEntry> _items;
    private readonly Func<string, StartupItemEntry?, bool, StartupToggleResult>? _toggleHandler;

    public InMemoryStartupAdapter(
        List<StartupItemEntry>? items = null,
        Func<string, StartupItemEntry?, bool, StartupToggleResult>? toggleHandler = null)
    {
        _items = items ?? new();
        _toggleHandler = toggleHandler;
    }

    public Task<IReadOnlyList<StartupItemEntry>> ListStartupItemsAsync(CancellationToken cancellation = default)
        => Task.FromResult((IReadOnlyList<StartupItemEntry>)_items.AsReadOnly());

    public Task<StartupToggleResult> ToggleStartupAsync(string itemId, bool enable, CancellationToken cancellation = default)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId);
        return Task.FromResult(_toggleHandler is { } h
            ? h(itemId, item, enable)
            : new StartupToggleResult(itemId, enable, false, "沙箱假适配器不执行真实启停"));
    }
}

/// <summary>一键加速策略（道系口径：只建议、不代做；高危提示、低风险不打扰）。</summary>
public sealed record SpeedupPolicy(
    /// <summary>内存占用达到该百分比时建议释放内存，与设置页默认告警值一致。</summary>
    double MemoryPressurePercent = 85.0,
    /// <summary>高风险启动项自动进「建议禁用」清单（仍需用户逐条确认才执行）。</summary>
    bool SuggestDisableHighRisk = true)
{
    /// <summary>标准建议文案：高危启动项。</summary>
    public const string HighRiskReason = "常见流氓推广位，建议禁用开机自启（需你确认后执行）";
    /// <summary>标准建议文案：中风险启动项。</summary>
    public const string MediumRiskReason = "未确认发布者，可自行判断是否禁用";
    /// <summary>标准建议文案：内存压力。</summary>
    public const string MemoryTrimReason = "物理内存占用偏高，可清理后台释放；不影响已保存数据";
}

/// <summary>一条加速建议（建议 + 理由文案，不含任何已执行状态）。</summary>
public sealed record SpeedupSuggestion(StartupItemEntry Item, string Reason);

/// <summary>一键加速计划：只列建议与依据，动作必须等用户确认。</summary>
public sealed record SpeedupPlan(
    IReadOnlyList<SpeedupSuggestion> DisableSuggestions,
    IReadOnlyList<SpeedupSuggestion> ReviewSuggestions,
    bool MemoryTrimSuggested,
    string? MemoryNote)
{
    /// <summary>页面摘要行：本轮共有几条可优化项（含内存建议），零时返回「已很清爽」。</summary>
    public string SummaryText
    {
        get
        {
            int total = DisableSuggestions.Count + ReviewSuggestions.Count + (MemoryTrimSuggested ? 1 : 0);
            return total == 0 ? "当前已很清爽，无需加速" : $"共 {total} 项可优化，逐条确认后执行";
        }
    }
}

/// <summary>一次加速动作的回执（适配层结果映射，界面直接展示）。</summary>
public sealed record SpeedupApplyResult(string ItemId, bool Success, string? Detail)
{
    public string DisplayText => Success ? "已禁用，下次开机生效" : (Detail ?? "操作未完成，请重试");
}

/// <summary>
/// 一键加速编排器：把启动项清单、风险分级与内存压力串成一份建议计划。
/// 三条红线：计划阶段绝不执行任何动作；执行只限用户逐条确认的条目；本产品自带组件不与第三方混算。
/// </summary>
public sealed class SpeedupCoordinator(IStartupControlAdapter adapter)
{
    /// <summary>产出加速计划（纯只读：不调用适配器的任何写动作）。</summary>
    public async Task<SpeedupPlan> PlanSpeedupAsync(
        SpeedupPolicy? policy = null,
        MemorySnapshot? memory = null,
        CancellationToken cancellation = default)
    {
        policy ??= new SpeedupPolicy();
        var disable = new List<SpeedupSuggestion>();
        var review = new List<SpeedupSuggestion>();

        foreach (var item in await adapter.ListStartupItemsAsync(cancellation))
        {
            if (!item.Enabled) continue; // 已禁用的不打扰
            switch (item.Risk)
            {
                case StartupRiskLevel.High when policy.SuggestDisableHighRisk:
                    disable.Add(new SpeedupSuggestion(item, item.RiskHint ?? SpeedupPolicy.HighRiskReason));
                    break;
                case StartupRiskLevel.Medium:
                    review.Add(new SpeedupSuggestion(item, item.RiskHint ?? SpeedupPolicy.MediumRiskReason));
                    break;
            }
        }

        bool trim = memory is { } m && m.PhysicalUsedPercent >= policy.MemoryPressurePercent;
        return new SpeedupPlan(disable, review, trim, trim ? SpeedupPolicy.MemoryTrimReason : null);
    }

    /// <summary>
    /// 执行禁用：只处理传入的确认清单，逐项经适配层执行并回填结果。
    /// 高风险项必须 userConfirmed=true 才放行（双保险；界面漏传视为未确认）。
    /// </summary>
    public async Task<IReadOnlyList<SpeedupApplyResult>> ApplyDisableAsync(
        IReadOnlyList<(string ItemId, bool UserConfirmed)> confirmed,
        CancellationToken cancellation = default)
    {
        var results = new List<SpeedupApplyResult>();
        foreach (var (itemId, confirmedByUser) in confirmed)
        {
            if (!confirmedByUser)
            {
                results.Add(new SpeedupApplyResult(itemId, false, "未确认，已跳过；加速动作必须逐条确认"));
                continue;
            }
            var r = await adapter.ToggleStartupAsync(itemId, enable: false, cancellation);
            results.Add(new SpeedupApplyResult(itemId, r.Success, r.Detail));
        }
        return results;
    }
}
