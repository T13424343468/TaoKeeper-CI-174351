using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TaoKeeper.Core.Modules;

/// <summary>软件来源渠道：winget 社区源优先，GitHub 官方发布兜底，官网直下手动引导。</summary>
public enum SoftwareSourceKind { WingetCommunity, GitHubRelease, OfficialPageManual }

/// <summary>一条软件条目（已安装清单或可升级清单共用，只读）。</summary>
public sealed record SoftwareEntry(
    string Id,
    string Name,
    string? Publisher,
    string? InstalledVersion,
    string? AvailableVersion,
    SoftwareSourceKind Source,
    /// <summary>来源校验状态：null=未核验；true=manifest/官方 checksums 哈希已过；false=哈希失配（必须拒绝安装）。</summary>
    bool? HashVerified = null,
    /// <summary>给用户看的来源说明，如「winget 社区源」「GitHub 官方 Releases」。</summary>
    string? SourceDisplay = null)
{
    /// <summary>是否存在可升级版本（需要两侧版本都有且可用比较器分出高低）。</summary>
    public bool HasUpgrade => InstalledVersion is { } ins && AvailableVersion is { } avail
        && SoftwareVersionComparer.Compare(ins, avail) < 0;
}

/// <summary>软件版本比较器：按 . - _ + 分段，数字段按数值比、文本段按字典序，段多者新。</summary>
public static class SoftwareVersionComparer
{
    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var pa = Split(a);
        var pb = Split(b);
        for (int i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            var sa = i < pa.Count ? pa[i] : "0";
            var sb = i < pb.Count ? pb[i] : "0";
            bool na = long.TryParse(sa, out var va);
            bool nb = long.TryParse(sb, out var vb);
            int r = (na, nb) switch
            {
                (true, true) => va.CompareTo(vb),
                (true, false) => 1,   // 数字段视为比文本段新（1.0 vs beta）
                (false, true) => -1,
                _ => string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase)
            };
            if (r != 0) return r;
        }
        return 0;
    }

    private static List<string> Split(string version) =>
        version.Split(new[] { '.', '-', '_', '+' }, StringSplitOptions.RemoveEmptyEntries).ToList();
}

/// <summary>升级策略设置（道系口径：静默升级默认关，用户逐项勾选后才静默）。</summary>
public sealed record SoftwareUpgradePolicy(
    bool SilentUpgradeAllowed = false,
    /// <summary>哈希校验失败时必须拒绝：硬规则，不受策略影响。</summary>
    bool RejectOnHashMismatch = true)
{
    /// <summary>决定一次升级是否可以免确认执行：策略允许静默 + 哈希已验证通过。</summary>
    public bool CanRunSilently(bool? hashVerified) =>
        SilentUpgradeAllowed && hashVerified == true;
}

/// <summary>安装/升级动作的执行结果（适配层回填，逻辑层只做判定与展示文案）。</summary>
public sealed record SoftwareActionResult(
    string SoftwareId,
    /// <summary>true=安装，false=卸载（升级=先装后置换，由适配层编排）。</summary>
    bool IsInstall,
    bool Success,
    /// <summary>来源校验状态：null=未核验（来源不提供哈希依据时）；true=校验通过；false=哈希失配（必须拒绝安装）。</summary>
    bool? HashVerified,
    /// <summary>给界面直接展示的结果文案，不泄露内部异常细节。</summary>
    string? Detail = null)
{
    /// <summary>哈希校验未过时的标准拒绝文案（透明之道：失败可见，不静默绕过）。</summary>
    public const string HashMismatchDetail = "安装包哈希校验未通过，已拒绝安装；来源信息可在软件详情页查看";
}

/// <summary>
/// 软件管家来源适配器契约（模块 M5 的可移植边界）。
/// Windows 真机由 winget 适配层实现；沙箱测试用内存假适配器验证策略与编排逻辑。
/// 适配层职责边界：只走官方渠道、安装包哈希校验、绝不做捆绑推广。
/// </summary>
public interface ISoftwareSourceAdapter
{
    /// <summary>列出已安装软件清单（含来源与版本）。</summary>
    Task<IReadOnlyList<SoftwareEntry>> ListInstalledAsync(CancellationToken cancellation = default);

    /// <summary>查询可升级清单。</summary>
    Task<IReadOnlyList<SoftwareEntry>> ListUpgradesAsync(CancellationToken cancellation = default);

    /// <summary>执行安装或升级；哈希校验未过必须返回失败结果且不落盘安装。</summary>
    Task<SoftwareActionResult> InstallAsync(SoftwareEntry software, bool allowSilent, CancellationToken cancellation = default);

    /// <summary>卸载（走系统正规卸载入口，不做进程强杀式删除）。</summary>
    Task<SoftwareActionResult> UninstallAsync(SoftwareEntry software, CancellationToken cancellation = default);
}

/// <summary>内存假适配器：用于策略与编排逻辑的沙箱测试，不涉及真实安装。</summary>
public sealed class InMemorySoftwareAdapter : ISoftwareSourceAdapter
{
    private readonly List<SoftwareEntry> _installed;
    private readonly List<SoftwareEntry> _upgrades;
    private readonly Func<SoftwareEntry, bool, SoftwareActionResult>? _installHandler;

    public InMemorySoftwareAdapter(
        List<SoftwareEntry>? installed = null,
        List<SoftwareEntry>? upgrades = null,
        Func<SoftwareEntry, bool, SoftwareActionResult>? installHandler = null)
    {
        _installed = installed ?? new();
        _upgrades = upgrades ?? new();
        _installHandler = installHandler;
    }

    public Task<IReadOnlyList<SoftwareEntry>> ListInstalledAsync(CancellationToken cancellation = default)
        => Task.FromResult((IReadOnlyList<SoftwareEntry>)_installed.AsReadOnly());

    public Task<IReadOnlyList<SoftwareEntry>> ListUpgradesAsync(CancellationToken cancellation = default)
        => Task.FromResult((IReadOnlyList<SoftwareEntry>)_upgrades.AsReadOnly());

    public Task<SoftwareActionResult> InstallAsync(SoftwareEntry software, bool allowSilent, CancellationToken cancellation = default)
        => Task.FromResult(_installHandler is { } h
            ? h(software, allowSilent)
            : new SoftwareActionResult(software.Id, true, false, software.HashVerified == true, "沙箱假适配器不执行真实安装"));

    public Task<SoftwareActionResult> UninstallAsync(SoftwareEntry software, CancellationToken cancellation = default)
        => Task.FromResult(new SoftwareActionResult(software.Id, false, true, true, "已卸载（模拟）"));
}

/// <summary>软件管家编排器：把清单、升级判定与策略串起来（不执行真实安装，动作由适配层完成）。</summary>
public sealed class SoftwareManagerCoordinator(ISoftwareSourceAdapter adapter)
{
    /// <summary>一次升级判定的产出：哪些可静默、哪些必须用户确认、哪些被哈希策略拦下。</summary>
    public sealed record UpgradePlan(
        IReadOnlyList<SoftwareEntry> SilentAllowed,
        IReadOnlyList<SoftwareEntry> NeedConfirm,
        IReadOnlyList<SoftwareEntry> BlockedByPolicy);

    public async Task<UpgradePlan> PlanUpgradesAsync(
        SoftwareUpgradePolicy policy,
        CancellationToken cancellation = default)
    {
        var upgrades = await adapter.ListUpgradesAsync(cancellation);
        var silent = new List<SoftwareEntry>();
        var confirm = new List<SoftwareEntry>();
        var blocked = new List<SoftwareEntry>();
        foreach (var s in upgrades)
        {
            if (!s.HasUpgrade) continue;
            if (s.HashVerified == false && policy.RejectOnHashMismatch) blocked.Add(s);
            else if (policy.CanRunSilently(s.HashVerified)) silent.Add(s);
            else confirm.Add(s);
        }
        return new UpgradePlan(silent, confirm, blocked);
    }
}
