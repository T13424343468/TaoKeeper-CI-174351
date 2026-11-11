using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>驱动页单行状态：该条目此刻可执行什么（界面按此出按钮，不给假开关）。</summary>
public enum DriverRowState
{
    UpToDate,           // 已是最新：无动作（原型「—」）
    UpgradeNeedConfirm, // 有新版：默认需用户逐项确认（高危设备/静默未开/哈希未核验）
    UpgradeSilent,      // 有新版：策略允许静默且哈希已过（仍显示确认按钮，静默只是免逐项弹窗）
    BlockedByPolicy,    // 哈希失配：标死不可执行（硬规则，确认也不放行）
}

/// <summary>驱动清册单行（绑定就绪）。</summary>
public sealed record DriverPageRow(
    string DeviceId,
    string DeviceName,
    string InstalledVersionText,
    string AvailableVersionText,
    string SourceText,
    DriverRowState State,
    /// <summary>升级前必须先执行的备份命令（pnputil 导出，真机由适配层执行）。</summary>
    string BackupCommand,
    string RollbackGuidance);

/// <summary>
/// 驱动升级页视图模型（最小可用形态）：驱动清册、更新候选、备份计划展示与逐项确认闸门。
/// 道系口径（与 Core 同源）：驱动不是垃圾，宁可不动不可出事——
/// 每次升级前必须备份；高危设备（显卡/芯片组/存储/网络）必须用户逐项确认；
/// 哈希失配标死不可执行；只走官方渠道（WUA/厂商官网），不装第三方改版驱动。
/// 本层零直写：升级动作一律经 DriverUpdateCoordinator 三道闸（备份→确认→升级），
/// 未确认就触发升级会被闸门拒绝（返回拒绝结果，不产生任何写动作）。
/// 未测/未扫/未知一律如实：未扫描态全空，界面显示「尚未扫描」。
/// </summary>
public sealed class DriverPageViewModel
{
    private readonly Dictionary<string, bool> _confirmedDeviceIds = new(StringComparer.Ordinal);
    private Dictionary<string, DriverUpdateEntry> _entryIndex =
        new(StringComparer.Ordinal);

    /// <summary>最近一次计划三分桶（null=尚未扫描）。</summary>
    public DriverUpdateCoordinator.DriverUpdatePlan? Plan { get; private set; }

    /// <summary>已扫描驱动条目总数（含已是最新）。</summary>
    public int ScannedCount { get; private set; }

    /// <summary>是否存在任何可升级项（可静默+需确认；拦截项单独展示）。</summary>
    public bool HasUpgrades => Plan is { } p && (p.SilentAllowed.Count > 0 || p.NeedConfirm.Count > 0);

    /// <summary>策略拦截数（哈希失配，硬规则）。</summary>
    public int BlockedCount => Plan?.BlockedByPolicy.Count ?? 0;

    /// <summary>概览文本（扫描后刷新；未扫描态固定文案）。</summary>
    public string SummaryText { get; private set; } = "尚未扫描驱动清单";

    /// <summary>清册行（含已是最新行；扫描后填充，按设备名升序稳定展示）。</summary>
    public IReadOnlyList<DriverPageRow> Rows { get; private set; } = Array.Empty<DriverPageRow>();

    /// <summary>
    /// 扫描驱动清册并产出升级计划：经 Core 编排器（唯一事实源），本层不重算策略。
    /// </summary>
    public async Task<DriverPageViewModel> LoadAsync(
        IDriverUpdateAdapter adapter,
        DriverUpdatePolicy? policy = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var effectivePolicy = policy ?? new DriverUpdatePolicy();

        var coordinator = new DriverUpdateCoordinator(adapter);
        var plan = await coordinator.PlanDriverUpdatesAsync(effectivePolicy, cancellation);
        var entries = await adapter.ListDriverUpdatesAsync(cancellation);

        Plan = plan;
        ScannedCount = entries.Count;
        _entryIndex = entries.ToDictionary(e => e.DeviceId, e => e, StringComparer.Ordinal);
        _confirmedDeviceIds.Clear(); // 新一轮扫描后旧确认失效（条目可能已变化），逐项确认重来

        Rows = entries
            .OrderBy(e => e.DeviceName, StringComparer.CurrentCulture)
            .Select(e =>
            {
                var state = e.HashVerified == false && effectivePolicy.RejectOnHashMismatch
                    ? DriverRowState.BlockedByPolicy
                    : e.HasUpgrade
                        ? (effectivePolicy.CanRunSilently(e)
                            ? DriverRowState.UpgradeSilent
                            : DriverRowState.UpgradeNeedConfirm)
                        : DriverRowState.UpToDate;
                return new DriverPageRow(
                    e.DeviceId,
                    e.DeviceName,
                    e.InstalledVersion ?? "未装驱动",
                    e.HasUpgrade ? e.AvailableVersion! : "已是最新",
                    SourceTextOf(e),
                    state,
                    DriverUpdateCoordinator.DriverUpdatePlan.BuildBackupPlan(e.DeviceId).ExportCommand,
                    DriverBackupPlan.StandardRollbackGuidance);
            })
            .ToList();

        SummaryText = HasUpgrades || BlockedCount > 0
            ? $"已扫描 {ScannedCount} 个设备，可升级 {plan.SilentAllowed.Count + plan.NeedConfirm.Count} 个" +
              (BlockedCount > 0 ? $"（哈希失配拦截 {BlockedCount} 个）" : string.Empty) +
              "；升级前自动备份，高危设备需逐项确认"
            : $"已扫描 {ScannedCount} 个设备，驱动全部为最新版本";
        return this;
    }

    /// <summary>用户逐项确认某设备升级（界面按钮触发；哈希失配条目确认无效，仍标死）。</summary>
    public bool ConfirmUpgrade(string deviceId)
    {
        if (_entryIndex.TryGetValue(deviceId, out var entry)
            && entry.HashVerified != false)
        {
            _confirmedDeviceIds[deviceId] = true;
            return true;
        }
        return false; // 未知设备或被策略拦死：确认不成立
    }

    /// <summary>撤销某设备的升级确认（确认窗口关掉时同步）。</summary>
    public bool RevokeConfirmation(string deviceId) => _confirmedDeviceIds.Remove(deviceId);

    /// <summary>某设备是否已确认（界面确认态展示用）。</summary>
    public bool IsConfirmed(string deviceId) => _confirmedDeviceIds.TryGetValue(deviceId, out var ok) && ok;

    /// <summary>
    /// 执行升级（确认闸门在先）：未确认、未知设备或哈希失配一律拒绝且不触达适配层（零写动作）；
    /// 已确认的经 Core 升级闸门（备份→高危确认双保险→执行）。
    /// </summary>
    public async Task<DriverActionResult> ExecuteUpgradeAsync(
        IDriverUpdateAdapter adapter,
        string deviceId,
        DriverUpdatePolicy? policy = null,
        string backupDirectory = "D:\\TaoKeeper\\DriverBackup",
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var effectivePolicy = policy ?? new DriverUpdatePolicy();

        if (!_entryIndex.TryGetValue(deviceId, out var entry))
            return Refuse(deviceId, "未知设备，未执行任何动作");

        if (entry.HashVerified == false && effectivePolicy.RejectOnHashMismatch)
            return Refuse(deviceId, DriverActionResult.HashMismatchDetail); // 标死：确认也不放行

        if (!_confirmedDeviceIds.TryGetValue(deviceId, out var confirmed) || !confirmed)
            return Refuse(deviceId, "尚未经你确认，未执行任何动作（驱动升级必须逐项确认）");

        return await new DriverUpdateCoordinator(adapter).UpgradeAsync(
            entry, effectivePolicy, backupDirectory, userConfirmed: confirmed, cancellation);

        static DriverActionResult Refuse(string id, string detail) =>
            new(id, Success: false, BackupCompleted: false, detail);
    }

    /// <summary>来源文案：WUA=Windows 更新官方通道；pnputil 包=厂商官网。</summary>
    public static string SourceTextOf(DriverUpdateEntry entry) => entry.Kind switch
    {
        DriverUpdateKind.WuaDriver => "Windows 更新",
        _ => "厂商官网",
    };

    /// <summary>行状态 → 主题色（最新=墨灰、需确认=金、可静默=竹青、拦截=朱砂），随主题即时取值。</summary>
    public string StateColorOf(DriverRowState state, ThemeTokens tokens) => state switch
    {
        DriverRowState.BlockedByPolicy => tokens.Cinnabar,
        DriverRowState.UpgradeNeedConfirm => tokens.Gold,
        DriverRowState.UpgradeSilent => tokens.Bamboo,
        _ => tokens.Ink2,
    };
}
