using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoKeeper.Core.Modules;

/// <summary>
/// 页面文件方案编排（纯逻辑，可移植层）：把用户档位映射为真实 PagingFiles 多串条目并给出可读摘要。
/// 只做编排与校验，不触碰注册表；写系统设置的动作属于用户确认后的适配层（MemPage.PageFileApplyExecutor）。
/// </summary>
public static class PageFilePlanComposer
{
    /// <summary>Windows「自动管理所有驱动器的分页文件大小」在注册表 PagingFiles 中的表示。</summary>
    public const string SystemManagedEntry = @"?:\pagefile.sys";

    /// <summary>把内存页方案键（MemPage 按钮 Tag）映射为方案；custom/未知返回 null 由调用方如实说明。</summary>
    public static PageFileSettings? FromPlanKey(string planKey) => planKey switch
    {
        "smart" => new PageFileSettings(PageFilePreset.SmartRecommendation, null, null, null),
        "perf" => new PageFileSettings(PageFilePreset.PerformanceFirst, null, null, null),
        "space" => new PageFileSettings(PageFilePreset.SpaceFirst, null, null, null),
        "system" => new PageFileSettings(PageFilePreset.SystemManaged, null, null, null),
        _ => null, // custom：本版无自定义输入界面，调用方如实回执，不编造参数
    };

    /// <summary>
    /// 编排目标 PagingFiles 条目：系统托管=自动管理串；其余档位=按 Core 建议值固定在盘位上。
    /// 无建议值的档位（custom/未知）抛 ArgumentException，绝不编参数写系统。
    /// </summary>
    public static IReadOnlyList<string> ComposeTargetEntries(PageFileSettings settings, long physicalMemoryBytes, string systemDrive)
    {
        if (settings.Preset == PageFilePreset.SystemManaged) return new[] { SystemManagedEntry };
        // SuggestedValues 吃 MB（与 Core 口径一致），调用方传的是字节，先换算
        var (initialMb, maximumMb) = settings.SuggestedValues(physicalMemoryBytes / (1024L * 1024L))
            ?? throw new ArgumentException("该档位没有可计算的页面文件参数，拒绝写系统设置。");
        var drive = string.IsNullOrWhiteSpace(settings.DriveLetter)
            ? (systemDrive ?? "C").TrimEnd(':').ToUpperInvariant()
            : settings.DriveLetter.TrimEnd(':').ToUpperInvariant();
        return new[] { $"{drive}:\\pagefile.sys {initialMb} {maximumMb}" };
    }

    /// <summary>现值人类可读摘要：托管态/固定大小/空，用于回执对照。</summary>
    public static string Describe(IReadOnlyList<string?>? currentEntries)
    {
        if (currentEntries is null || currentEntries.Count == 0) return "（未配置）";
        if (currentEntries.Any(e => string.Equals(e?.Trim(), SystemManagedEntry, StringComparison.OrdinalIgnoreCase)))
            return "系统托管（自动管理所有驱动器）";
        return string.Join("；", currentEntries.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!.Trim()));
    }

    /// <summary>目标值与现值逐串相等判定（大小写与连续空白不敏感），相同则不写系统。</summary>
    public static bool SameAsCurrent(IReadOnlyList<string> target, IReadOnlyList<string?> current)
        => target.Count == current.Count
           && target.Select(NormalizeEntry)
               .SequenceEqual(current.Select(NormalizeEntry), StringComparer.OrdinalIgnoreCase);

    /// <summary>条目归一：按空白切分后以单空格重连（"C:\pagefile.sys  2048 4096" ≡ "c:\pagefile.sys 2048 4096"）。</summary>
    private static string NormalizeEntry(string? entry)
        => string.Join(' ', (entry ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
