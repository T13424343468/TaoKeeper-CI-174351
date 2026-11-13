using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>
/// 软件管家页视图模型（UI 框架零依赖，WinUI3 壳直接绑定）：
/// 走 Core 的 SoftwareManagerCoordinator（唯一事实源）做升级三分桶——
/// 静默可跑 / 需用户确认 / 策略拦截（哈希失配硬规则，确认也不放行）。
/// 本模型零写动作：只产出待执行清单与展示文案，真实安装由适配层在用户确认后执行。
/// </summary>
public sealed class SoftwarePageViewModel
{
    /// <summary>升级策略（道系口径：静默升级默认关；哈希失配必须拒绝，不受策略影响）。</summary>
    public SoftwareUpgradePolicy Policy { get; private set; } = new();

    /// <summary>最近一次三分桶结果（null=尚未扫描）。</summary>
    public SoftwareManagerCoordinator.UpgradePlan? Plan { get; private set; }

    /// <summary>已安装软件总数（最近一次扫描）。</summary>
    public int InstalledCount { get; private set; }

    /// <summary>概览文本（扫描后刷新；未扫描态固定文案）。</summary>
    public string SummaryText { get; private set; } = "尚未扫描软件清单";

    /// <summary>静默可跑桶（策略允许静默且哈希已验证通过）。</summary>
    public IReadOnlyList<SoftwareEntry> SilentAllowed { get; private set; } = Array.Empty<SoftwareEntry>();

    /// <summary>需确认桶（策略未开静默或哈希未核验）。</summary>
    public IReadOnlyList<SoftwareEntry> NeedConfirm { get; private set; } = Array.Empty<SoftwareEntry>();

    /// <summary>策略拦截桶（哈希失配：硬规则，确认也不放行，界面标死不可执行）。</summary>
    public IReadOnlyList<SoftwareEntry> BlockedByPolicy { get; private set; } = Array.Empty<SoftwareEntry>();

    /// <summary>待执行升级清单（静默+确认两桶；拦截项根本不进）。</summary>
    public IReadOnlyList<UpgradeAction> UpgradeActions { get; private set; } = Array.Empty<UpgradeAction>();

    /// <summary>是否存在任何可升级项（三分桶任一非空）。</summary>
    public bool HasUpgrades => SilentAllowed.Count > 0 || NeedConfirm.Count > 0 || BlockedByPolicy.Count > 0;

    /// <summary>是否存在策略拦截项（界面显示拦截提示区用）。</summary>
    public bool HasBlocked => BlockedByPolicy.Count > 0;

    /// <summary>
    /// 扫描并三分桶：经 Core 编排器取已安装清单与升级计划，全走唯一事实源，本层不重算策略。
    /// </summary>
    public async Task<SoftwarePageViewModel> BuildAsync(
        ISoftwareSourceAdapter adapter,
        SoftwareUpgradePolicy? policy = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        Policy = policy ?? new SoftwareUpgradePolicy();

        var installed = await adapter.ListInstalledAsync(cancellation);
        var coordinator = new SoftwareManagerCoordinator(adapter);
        var plan = await coordinator.PlanUpgradesAsync(Policy, cancellation);

        InstalledCount = installed.Count;
        Plan = plan;
        SilentAllowed = plan.SilentAllowed;
        NeedConfirm = plan.NeedConfirm;
        BlockedByPolicy = plan.BlockedByPolicy;
        UpgradeActions = plan.SilentAllowed.Select(s => new UpgradeAction(s, AllowSilent: true))
            .Concat(plan.NeedConfirm.Select(s => new UpgradeAction(s, AllowSilent: false)))
            .ToList();

        SummaryText = HasUpgrades
            ? $"已安装 {InstalledCount} 个，可升级 {TotalUpgradable} 个（静默可跑 {SilentAllowed.Count} / 需确认 {NeedConfirm.Count} / 策略拦截 {BlockedByPolicy.Count}）"
            : $"已安装 {InstalledCount} 个，全部为最新版本";
        return this;
    }

    /// <summary>可升级总数（三分桶合计）。</summary>
    public int TotalUpgradable => SilentAllowed.Count + NeedConfirm.Count + BlockedByPolicy.Count;

    /// <summary>单条升级是否可免确认执行（按当前策略与条目哈希状态判定）。</summary>
    public bool IsSilentAllowed(SoftwareEntry entry) => Policy.CanRunSilently(entry.HashVerified);

    /// <summary>待执行动作（升级=先装后置换由适配层编排；卸载另走系统正规卸载入口）。</summary>
    public readonly record struct UpgradeAction(SoftwareEntry Software, bool AllowSilent);

    /// <summary>来源文案（内部/调试口径；G7 用户界面零外显，呈现层一律走 <see cref="SafetyText"/>）。</summary>
    public static string SourceText(SoftwareEntry entry) => entry.SourceDisplay ?? entry.Source switch
    {
        SoftwareSourceKind.WingetCommunity => "winget 社区源",
        SoftwareSourceKind.GitHubRelease => "GitHub 官方发布",
        _ => "官网直下（手动确认）",
    };

    /// <summary>
    /// 用户可懂的安全状态（G7：来源渠道名与哈希术语不外显，安全校验后台完成、说人话呈现）。
    /// 失配=已拦截（不可装，与策略硬规则一致）；已校验=安全校验已过；未核验=装前自动校验。
    /// </summary>
    public static string SafetyText(SoftwareEntry entry) => entry.HashVerified switch
    {
        false => "安全校验未过 · 已拦截",
        true => "安全校验已通过",
        null => "装前自动安全校验",
    };

    /// <summary>版本展示文案：可升级显「现版 → 新版」，否则现版（缺省兜底）。</summary>
    public static string VersionText(SoftwareEntry entry) => entry.HasUpgrade
        ? $"{entry.InstalledVersion} → {entry.AvailableVersion}"
        : entry.InstalledVersion ?? "未知版本";

    /// <summary>条目状态色（拦截=朱砂、静默可跑=竹青、需确认=金），随主题即时取值。</summary>
    public string StatusColorOf(SoftwareEntry entry, ThemeTokens tokens) =>
        entry.HashVerified == false ? tokens.Cinnabar
        : IsSilentAllowed(entry) ? tokens.Bamboo
        : tokens.Gold;
}
