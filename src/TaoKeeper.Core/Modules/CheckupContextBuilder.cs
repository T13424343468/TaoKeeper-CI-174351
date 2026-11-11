using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Models;

namespace TaoKeeper.Core.Modules;

/// <summary>
/// 体检上下文汇总器（体检页评分打通收口）：把各模块的真实扫描产出汇成 <see cref="HealthContext"/>。
/// 未扫描/未核验的维度保持 null（「本轮未测」语义），绝不以空值或零冒充实测——空上下文会被
/// 体检页识别为未测态，混入 0 会被误当成实测「无垃圾/无重复/无启动项」。
/// 汇总器只读零写动作；系统盘余量等系统能力由适配层提供数值，这里只做汇总、换算与防呆校验。
/// </summary>
public static class CheckupContextBuilder
{
    /// <summary>
    /// 汇总体检上下文。各参数独立可选：传 null 即该维度本轮未测。
    /// </summary>
    /// <param name="junkScan">垃圾清理扫描结果（JunkRuleEngine）；可清理量取 TotalBytes。</param>
    /// <param name="duplicateGroups">重复文件识别结果（DuplicateFileFinder）；可释放量按组求和（保留组内一份后）。</param>
    /// <param name="startupItems">启动项清单（适配层枚举）；仅统计启用态条目——禁用项不随开机启动、不构成开机负担。</param>
    /// <param name="systemDiskFreeBytes">系统盘剩余字节（适配层提供）。</param>
    /// <param name="systemDiskTotalBytes">系统盘总容量字节（适配层提供）。</param>
    /// <param name="storageSensePlan">存储感知当前方案；Enabled 决定体检提示。</param>
    /// <param name="pageFileSettings">页面文件目标设置（M9）；用于虚拟内存核对提示。</param>
    /// <param name="physicalMemoryBytes">物理内存字节数；页面文件自定义参数核对需要，缺失时跳过该核对（防假报错）。</param>
    public static HealthContext Build(
        JunkScanResult? junkScan = null,
        IReadOnlyList<DuplicateGroup>? duplicateGroups = null,
        IReadOnlyList<StartupItemEntry>? startupItems = null,
        long? systemDiskFreeBytes = null,
        long? systemDiskTotalBytes = null,
        StorageSensePlan? storageSensePlan = null,
        PageFileSettings? pageFileSettings = null,
        long? physicalMemoryBytes = null)
    {
        ValidateDiskBytes(systemDiskFreeBytes, systemDiskTotalBytes);

        var junkBytes = junkScan?.TotalBytes;
        var duplicateBytes = duplicateGroups is null
            ? null
            : (long?)duplicateGroups.Sum(g => g.ReclaimableBytes);

        // 启动项计数：只计启用态（禁用项不构成开机负担）；本产品组件同样随开机启动，照常计入总数。
        var startupCount = startupItems?.Count(e => e.Enabled);

        var freePercent = systemDiskFreeBytes is { } free && systemDiskTotalBytes is { } total
            ? (double?)(100.0 * free / total)
            : null;

        return new HealthContext(
            JunkReclaimableBytes: junkBytes,
            DuplicateReclaimableBytes: duplicateBytes,
            StartupItemCount: startupCount,
            SystemDiskFreePercent: freePercent,
            VirtualMemoryHint: VirtualMemoryHint(pageFileSettings, physicalMemoryBytes),
            StorageSenseEnabled: storageSensePlan?.Enabled);
    }

    /// <summary>系统盘字节数防呆：总量/余量非法（非正值总量、负余量、余量大于总量）一律报红，不换算出假百分比。</summary>
    private static void ValidateDiskBytes(long? systemDiskFreeBytes, long? systemDiskTotalBytes)
    {
        if (systemDiskTotalBytes is { } total && total <= 0)
            throw new ArgumentOutOfRangeException(nameof(systemDiskTotalBytes), total, "系统盘总容量必须为正数");
        if (systemDiskFreeBytes is { } free && free < 0)
            throw new ArgumentOutOfRangeException(nameof(systemDiskFreeBytes), free, "系统盘剩余字节不得为负");
        if (systemDiskFreeBytes is { } f && systemDiskTotalBytes is { } t && f > t)
            throw new ArgumentOutOfRangeException(nameof(systemDiskFreeBytes), f, $"剩余字节 {f} 不得大于总容量 {t}");
    }

    /// <summary>
    /// 虚拟内存核对提示（体检规则 virtual-memory-hint 只转述，不代做判断）：
    /// 完全关闭页面文件属危险设置，固定提示；其余经 PageFileSettingsValidator 校验，汇总错误与警告作为提示；
    /// 自定义参数核对依赖物理内存，物理内存未知时跳过核对（防止按 0 字节假报「超出上限」）。
    /// </summary>
    private static string? VirtualMemoryHint(PageFileSettings? settings, long? physicalMemoryBytes)
    {
        if (settings is null)
            return null;

        if (settings.FullyDisabled)
            return "页面文件已完全关闭：部分大型程序可能无法启动，崩溃时也无法生成转储，建议改回系统托管或智能推荐";

        // 物理内存未知时不做参数核对，避免以 0 字节内存得出失真的上限判定。
        if (physicalMemoryBytes is not { } physical || physical <= 0)
            return null;

        var validation = PageFileSettingsValidator.Validate(settings, physical);
        var messages = validation.Errors.Concat(validation.Warnings).ToList();
        return messages.Count == 0
            ? null
            : $"页面文件设置有可优化项：{string.Join("；", messages)}";
    }
}
