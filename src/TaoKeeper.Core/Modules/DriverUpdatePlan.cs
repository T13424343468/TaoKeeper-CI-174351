using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TaoKeeper.Core.Modules;

/// <summary>驱动升级来源通道：Windows Update 驱动（WUA）优先，厂商官网驱动包（pnputil 安装）兜底。</summary>
public enum DriverUpdateKind { WuaDriver, VendorPackagePnpUtil }

/// <summary>
/// 一条驱动升级条目（只读评估，不落任何真实系统状态）。
/// 危险设备（显卡/芯片组/存储/网络）默认按高危处理：升级前必须备份且必须用户逐项确认。
/// </summary>
public sealed record DriverUpdateEntry(
    string DeviceId,
    string DeviceName,
    /// <summary>当前已装驱动版本（可能为空：设备缺驱动）。</summary>
    string? InstalledVersion,
    /// <summary>候选新版本（可能为空：来源未给出）。</summary>
    string? AvailableVersion,
    DriverUpdateKind Kind,
    /// <summary>是否高危设备（显卡/芯片组/存储/网络）；由适配层按设备类型判定，逻辑层不猜。</summary>
    bool IsCriticalDevice,
    /// <summary>驱动包哈希校验状态：null=未核验；true=官方哈希已过；false=失配（必须拒绝）。</summary>
    bool? HashVerified = null)
{
    /// <summary>是否存在可用的新版本（两侧版本都有且不等）。</summary>
    public bool HasUpgrade => InstalledVersion is { } ins && AvailableVersion is { } avail
        && ins != avail;
}

/// <summary>
/// 驱动升级策略（道系口径：驱动不是垃圾，宁可不动，不可出事）。
/// 静默升级默认关；高危设备永不静默；备份是硬前置，没有备份计划就不放行升级。
/// </summary>
public sealed record DriverUpdatePolicy(
    bool SilentUpgradeAllowed = false,
    /// <summary>哈希失配必须拒绝：硬规则，不受策略影响。</summary>
    bool RejectOnHashMismatch = true,
    /// <summary>升级前必须导出当前驱动备份（pnputil /export-driver）：硬规则。</summary>
    bool RequireBackupBeforeUpgrade = true)
{
    /// <summary>判定一次驱动升级是否可免确认执行：策略允许静默 + 非高危 + 哈希已过。</summary>
    public bool CanRunSilently(DriverUpdateEntry entry) =>
        SilentUpgradeAllowed
        && !entry.IsCriticalDevice
        && entry.HashVerified == true;
}

/// <summary>一次驱动备份计划的产出：给适配层执行的 pnputil 导出命令与回滚指引（只读文本，不执行）。</summary>
public sealed record DriverBackupPlan(
    string DeviceId,
    /// <summary>导出当前驱动包的 pnputil 命令行（真机由适配层执行，逻辑层只生成文本）。</summary>
    string ExportCommand,
    /// <summary>回滚指引文案：设备管理器回退驱动 / pnputil 还原，给界面直接展示。</summary>
    string RollbackGuidance)
{
    /// <summary>标准回滚指引：道系口径不静默删改，回滚动作永远可见可确认。</summary>
    public const string StandardRollbackGuidance =
        "升级异常时：设备管理器 → 该设备 → 属性 → 驱动程序 → 回退驱动；或用备份目录 pnputil /add-driver 还原";
}

/// <summary>一次驱动升级动作的执行结果（适配层回填，逻辑层只做判定与展示文案）。</summary>
public sealed record DriverActionResult(
    string DeviceId,
    bool Success,
    /// <summary>升级前是否已完成驱动备份（未备份的高危升级必须失败回填）。</summary>
    bool BackupCompleted,
    /// <summary>给界面直接展示的结果文案，不泄露内部异常细节。</summary>
    string? Detail = null)
{
    /// <summary>未备份即尝试升级时的标准拒绝文案（宁可不动，不可出事）。</summary>
    public const string NoBackupDetail = "未完成驱动备份，已拒绝升级；请先在驱动备份中导出当前驱动";
    /// <summary>哈希失配时的标准拒绝文案。</summary>
    public const string HashMismatchDetail = "驱动包哈希校验未通过，已拒绝安装；来源信息可在驱动详情页查看";
}

/// <summary>
/// 驱动升级适配器契约（模块 M6 的可移植边界）。
/// Windows 真机由 WUA API / pnputil 适配层实现；沙箱测试用内存假适配器验证策略与编排逻辑。
/// 适配层职责边界：只走官方渠道（WUA/厂商官网）、驱动包哈希校验、升级前备份、绝不捆绑。
/// </summary>
public interface IDriverUpdateAdapter
{
    /// <summary>列出可升级的驱动条目（只读评估，不改动系统）。</summary>
    Task<IReadOnlyList<DriverUpdateEntry>> ListDriverUpdatesAsync(CancellationToken cancellation = default);

    /// <summary>导出指定设备当前驱动的备份；返回是否成功（真机写备份目录，逻辑层只记录结果）。</summary>
    Task<bool> BackupDriverAsync(string deviceId, string backupDirectory, CancellationToken cancellation = default);

    /// <summary>执行驱动升级；编排器保证调用前已备份，适配层仍需二次校验（双保险）。</summary>
    Task<DriverActionResult> ApplyDriverUpdateAsync(DriverUpdateEntry driver, CancellationToken cancellation = default);
}

/// <summary>内存假适配器：用于策略与编排逻辑的沙箱测试，不涉及真实驱动操作。</summary>
public sealed class InMemoryDriverUpdateAdapter : IDriverUpdateAdapter
{
    private readonly List<DriverUpdateEntry> _updates;
    private readonly Func<DriverUpdateEntry, bool>? _backupHandler;
    private readonly Func<DriverUpdateEntry, DriverActionResult>? _applyHandler;

    public InMemoryDriverUpdateAdapter(
        List<DriverUpdateEntry>? updates = null,
        Func<DriverUpdateEntry, bool>? backupHandler = null,
        Func<DriverUpdateEntry, DriverActionResult>? applyHandler = null)
    {
        _updates = updates ?? new();
        _backupHandler = backupHandler;
        _applyHandler = applyHandler;
    }

    public Task<IReadOnlyList<DriverUpdateEntry>> ListDriverUpdatesAsync(CancellationToken cancellation = default)
        => Task.FromResult((IReadOnlyList<DriverUpdateEntry>)_updates.AsReadOnly());

    public Task<bool> BackupDriverAsync(string deviceId, string backupDirectory, CancellationToken cancellation = default)
        => Task.FromResult(_backupHandler is { } h
            ? h(_updates.FirstOrDefault(d => d.DeviceId == deviceId) ?? new DriverUpdateEntry(deviceId, deviceId, null, null, DriverUpdateKind.WuaDriver, false))
            : false);

    public Task<DriverActionResult> ApplyDriverUpdateAsync(DriverUpdateEntry driver, CancellationToken cancellation = default)
        => Task.FromResult(_applyHandler is { } h
            ? h(driver)
            : new DriverActionResult(driver.DeviceId, false, false, "沙箱假适配器不执行真实驱动升级"));
}

/// <summary>
/// 驱动管家编排器：把清单、备份、高危确认与升级动作串起来。
/// 本编排器只产出计划与判定，真实执行永远经过：备份 → （高危）用户确认 → 升级 三道闸。
/// </summary>
public sealed class DriverUpdateCoordinator(IDriverUpdateAdapter adapter)
{
    /// <summary>一次驱动升级计划的产出：哪些可静默、哪些必须用户确认、哪些被哈希/备份策略拦下。</summary>
    public sealed record DriverUpdatePlan(
        IReadOnlyList<DriverUpdateEntry> SilentAllowed,
        IReadOnlyList<DriverUpdateEntry> NeedConfirm,
        IReadOnlyList<DriverUpdateEntry> BlockedByPolicy,
        /// <summary>与 NeedConfirm/SilentAllowed 一一对应的备份计划（升级前必须先执行对应导出）。</summary>
        IReadOnlyList<DriverBackupPlan> BackupPlans)
    {
        /// <summary>给定设备的标准备份命令（pnputil 导出，真机适配层执行）。</summary>
        public static DriverBackupPlan BuildBackupPlan(string deviceId) =>
            new(deviceId, $"pnputil /export-driver \"{deviceId}\" <备份目录>", DriverBackupPlan.StandardRollbackGuidance);
    }

    public async Task<DriverUpdatePlan> PlanDriverUpdatesAsync(
        DriverUpdatePolicy policy,
        CancellationToken cancellation = default)
    {
        var updates = await adapter.ListDriverUpdatesAsync(cancellation);
        var silent = new List<DriverUpdateEntry>();
        var confirm = new List<DriverUpdateEntry>();
        var blocked = new List<DriverUpdateEntry>();
        var backups = new List<DriverBackupPlan>();

        foreach (var d in updates)
        {
            if (!d.HasUpgrade) continue;
            if (policy.RequireBackupBeforeUpgrade)
                backups.Add(DriverUpdatePlan.BuildBackupPlan(d.DeviceId));

            if (d.HashVerified == false && policy.RejectOnHashMismatch)
            {
                blocked.Add(d);
                continue;
            }
            if (policy.CanRunSilently(d)) silent.Add(d);
            else confirm.Add(d);
        }
        return new DriverUpdatePlan(silent, confirm, blocked, backups);
    }

    /// <summary>
    /// 升级前闸门：备份未完成一律拒绝（返回 NoBackup 结果），高危设备永远要求确认标志。
    /// 适配层收到 <paramref name="userConfirmed"/>=false 的高危升级也必须拒绝（双保险）。
    /// </summary>
    public async Task<DriverActionResult> UpgradeAsync(
        DriverUpdateEntry driver,
        DriverUpdatePolicy policy,
        string backupDirectory,
        bool userConfirmed = false,
        CancellationToken cancellation = default)
    {
        if (policy.RequireBackupBeforeUpgrade)
        {
            var backupOk = await adapter.BackupDriverAsync(driver.DeviceId, backupDirectory, cancellation);
            if (!backupOk)
                return new DriverActionResult(driver.DeviceId, false, BackupCompleted: false, DriverActionResult.NoBackupDetail);
        }

        if (driver.HashVerified == false && policy.RejectOnHashMismatch)
            return new DriverActionResult(driver.DeviceId, false, BackupCompleted: true, DriverActionResult.HashMismatchDetail);

        if (driver.IsCriticalDevice && !userConfirmed)
            return new DriverActionResult(driver.DeviceId, false, BackupCompleted: true, "高危设备驱动升级需要逐项确认后再执行");

        return await adapter.ApplyDriverUpdateAsync(driver, cancellation);
    }
}
