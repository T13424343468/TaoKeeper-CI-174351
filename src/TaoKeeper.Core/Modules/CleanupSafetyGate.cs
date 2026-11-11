using System;
using System.Collections.Generic;

namespace TaoKeeper.Core.Modules;

/// <summary>
/// 清理安全闸门（确认闸门 + 硬拒绝的唯一事实源）：
/// 执行编排 CleanupCoordinator 与界面预检（确认队列）共用同一套判定，
/// 不允许两处各写一份导致口径漂移。判定本身零 IO、纯函数。
/// </summary>
public static class CleanupSafetyGate
{
    /// <summary>受保护目录名（大小写不敏感）：命中任何路径段的项一律硬拒绝。</summary>
    public static readonly ISet<string> DefaultProtectedNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData",
            "$RECYCLE.BIN", "System Volume Information"
        };

    /// <summary>
    /// 判定一条清理动作能否执行。返回 null=可执行；否则给出拒绝理由（界面可直接展示）。
    /// 铁律：未逐条确认绝不执行；受保护路径与盘符根即使用户确认也不放行。
    /// </summary>
    public static string? Evaluate(string fullPath, bool confirmed,
        IEnumerable<string>? protectedNames = null)
    {
        if (!confirmed) return "拒绝：未逐条确认，绝不代删";
        if (string.IsNullOrWhiteSpace(fullPath)) return "拒绝：空路径";

        var names = protectedNames as ISet<string> ?? new HashSet<string>(
            protectedNames ?? DefaultProtectedNames, StringComparer.OrdinalIgnoreCase);

        var normalized = fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, '/', '\\');
        if (string.IsNullOrEmpty(normalized)) return "拒绝：空路径";
        // 盘符根/根目录判定：手工按两种分隔符找父（System.IO.Path 在非 Windows 上不认 '\'，
        // 会把 D:\x 误判为无父目录）。无分隔符（如 "C:"）或本身就是 "/" 一律视为根。
        int lastSep = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('\\'));
        if (lastSep < 0 || (lastSep == 0 && normalized[0] == '/'))
            return "拒绝：盘符根/根目录不作为清理对象";

        var segments = normalized.Split('/', '\\');
        foreach (var segment in segments)
        {
            if (segment.Length > 0 && names.Contains(segment))
                return $"拒绝：受保护路径（{segment}），用户确认也不放行";
        }
        return null;
    }
}
