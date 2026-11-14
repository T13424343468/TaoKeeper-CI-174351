using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoKeeper.Core.Modules;

/// <summary>
/// 一次双源合并的审计留痕（原值只进审计，绝不外显给用户）：
/// 每组同软件的合并理由与双方原始来源说明（winget/注册表），可回溯可核对。
/// </summary>
public sealed record SoftwareMergeAudit(
    string Name,
    string Publisher,
    /// <summary>合并理由：双源同软件 / 仅 winget / 仅注册表。</summary>
    string Reason,
    /// <summary>winget 侧原始来源说明（缺席为 null）。</summary>
    string? WingetSourceDisplay,
    /// <summary>注册表侧原始来源说明（缺席为 null）。</summary>
    string? RegistrySourceDisplay);

/// <summary>
/// 软件管家已装清单双源合并（W7）：winget 社区源与注册表卸载登记按「名称+发布者」归并，
/// 择优保留——HashVerified true &gt; null &gt; false（安全校验状态优先），字段取胜者、空缺由对方补齐；
/// 用户侧来源说明（SourceDisplay）一律置 null（G7 来源零外显），原值只进审计留痕。
/// 纯逻辑层：只合并不外发、零写动作。
/// </summary>
public static class SoftwareSourceMerger
{
    /// <summary>
    /// 合并两份已装清单。winget 为主源（版本与可升级判定权威），注册表为对照源（官方卸载入口）。
    /// 返回用户侧清单（SourceDisplay 全 null）与审计留痕（原值）。
    /// </summary>
    public static (IReadOnlyList<SoftwareEntry> Entries, IReadOnlyList<SoftwareMergeAudit> Audit) Merge(
        IReadOnlyList<SoftwareEntry>? winget, IReadOnlyList<SoftwareEntry>? registry)
    {
        winget ??= Array.Empty<SoftwareEntry>();
        registry ??= Array.Empty<SoftwareEntry>();

        var byKey = new Dictionary<string, List<TaggedEntry>>(StringComparer.Ordinal);
        foreach (var entry in winget) Index(byKey, entry, isWingetSide: true);
        foreach (var entry in registry) Index(byKey, entry, isWingetSide: false);

        var entries = new List<SoftwareEntry>(byKey.Count);
        var audit = new List<SoftwareMergeAudit>(byKey.Count);
        foreach (var (_, group) in byKey)
        {
            var wingetSide = group.Where(static g => g.IsWingetSide).ToList();
            var registrySide = group.Where(static g => !g.IsWingetSide).ToList();
            audit.Add(new SoftwareMergeAudit(
                group[0].Entry.Name,
                group[0].Entry.Publisher ?? string.Empty,
                wingetSide.Count > 0 && registrySide.Count > 0
                    ? "双源同软件，字段择优"
                    : wingetSide.Count > 0 ? "仅 winget 社区源" : "仅注册表登记",
                wingetSide.FirstOrDefault()?.Entry.SourceDisplay,
                registrySide.FirstOrDefault()?.Entry.SourceDisplay));

            // 择优：HashVerified true > null > false（安全校验状态优先；同档不覆盖先到者）
            var merged = group[0];
            foreach (var candidate in group.Skip(1))
                if (Rank(candidate.Entry.HashVerified) < Rank(merged.Entry.HashVerified))
                    merged = candidate;

            // 字段空缺由另一侧补齐（发布者/现版本/可升级版本）
            foreach (var other in group)
            {
                if (ReferenceEquals(other, merged)) continue;
                merged = merged.Entry.Publisher is null || merged.Entry.InstalledVersion is null || merged.Entry.AvailableVersion is null
                    ? new TaggedEntry(merged.Entry with
                    {
                        Publisher = merged.Entry.Publisher ?? other.Entry.Publisher,
                        InstalledVersion = merged.Entry.InstalledVersion ?? other.Entry.InstalledVersion,
                        AvailableVersion = merged.Entry.AvailableVersion ?? other.Entry.AvailableVersion,
                    }, merged.IsWingetSide)
                    : merged;
            }

            // G7 来源零外显：用户侧说明一律置 null（原值已留审计）
            entries.Add(merged.Entry with { SourceDisplay = null });
        }
        return (entries, audit);
    }

    private static int Rank(bool? hashVerified) => hashVerified switch
    {
        true => 0,
        null => 1,
        false => 2,
    };

    private static void Index(Dictionary<string, List<TaggedEntry>> byKey, SoftwareEntry entry, bool isWingetSide)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = NormalizeName(entry.Name) + "|" + (entry.Publisher?.Trim().ToLowerInvariant() ?? string.Empty);
        if (!byKey.TryGetValue(key, out var group))
            byKey[key] = group = new List<TaggedEntry>();
        group.Add(new TaggedEntry(entry, isWingetSide));
    }

    private static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return string.Concat(name.Trim().Where(static c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant));
    }

    private sealed record TaggedEntry(SoftwareEntry Entry, bool IsWingetSide);
}
