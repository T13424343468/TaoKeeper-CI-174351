using System;
using System.Collections.Generic;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>一条 pnputil /enum-drivers 解析结果（已安装第三方驱动清册条目）。</summary>
public sealed record PnputilDriverRow(
    string PublishedName,
    string OriginalName,
    string Provider,
    string ClassName,
    string? Version,
    string? Date);

/// <summary>
/// pnputil /enum-drivers 输出解析器（纯逻辑，可沙箱测试）。
/// 输出是「标签: 值」分块结构，块间以空行分隔；系统语言会本地化标签
/// （英文 Published Name/Original Name/Provider Name/Class Name/Driver Version/Driver Date，
/// 中文 发布的名称/原始名称/提供程序名称/类名/驱动程序版本/驱动程序日期），两种都要认。
/// 未识别任何块的输出抛 FormatException 报红，绝不按空清单处理。
/// </summary>
public static class PnputilOutputParser
{
    /// <summary>标签候选：规范键 → 各语言标签（前缀匹配，不区分大小写）。</summary>
    private static readonly (string Key, string[] Labels)[] KnownLabels =
    {
        ("Published", new[] { "Published Name", "发布的名称", "发布名称" }),
        ("Original", new[] { "Original Name", "原始名称" }),
        ("Provider", new[] { "Provider Name", "提供程序名称", "提供者名称" }),
        ("Class", new[] { "Class Name", "类名", "类名称" }),
        ("Version", new[] { "Driver Version", "驱动程序版本", "驱动版本" }),
        ("Date", new[] { "Driver Date", "驱动程序日期", "驱动日期" }),
    };

    public static IReadOnlyList<PnputilDriverRow> ParseEnumDrivers(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var blocks = output.Split("\r\n\r\n", StringSplitOptions.RemoveEmptyEntries)
            ?? Array.Empty<string>();
        var rows = new List<PnputilDriverRow>();

        foreach (var block in blocks)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rawLine in block.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var label = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (label.Length == 0 || value.Length == 0) continue;

                // 每行独立匹配；一行只归一个字段，命中即停内层，外层继续找下一个键
                foreach (var (key, labels) in KnownLabels)
                {
                    foreach (var l in labels)
                    {
                        if (!label.StartsWith(l, StringComparison.OrdinalIgnoreCase)) continue;
                        fields[key] = value;
                        break;
                    }
                }
            }

            if (!fields.TryGetValue("Published", out var published) || published.Length == 0)
                continue; // 纯标题块或残缺块：不猜，跳过

            rows.Add(new PnputilDriverRow(
                published,
                fields.GetValueOrDefault("Original", string.Empty),
                fields.GetValueOrDefault("Provider", string.Empty),
                fields.GetValueOrDefault("Class", string.Empty),
                fields.TryGetValue("Version", out var v) ? v : null,
                fields.TryGetValue("Date", out var d) ? d : null));
        }

        if (rows.Count == 0 && LooksLikeError(output))
            throw new FormatException(
                $"pnputil /enum-drivers 输出未识别出任何驱动块，疑似命令失败或未覆盖的本地化标签；已拒绝按空清单处理。输出首行：{FirstLine(output)}");
        return rows;
    }

    /// <summary>输出疑似错误：剔除已知标题行后仍有正文却不含任何已知标签（真机报错/未覆盖本地化标签）。</summary>
    private static bool LooksLikeError(string output)
    {
        var contentLines = output.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0
                && !l.StartsWith("Microsoft PnP Utility", StringComparison.OrdinalIgnoreCase)
                && !l.StartsWith("Microsoft PnP 工具", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (contentLines.Count == 0) return false; // 只有标题（合法的零驱动输出）
        foreach (var line in contentLines)
        {
            foreach (var (key, labels) in KnownLabels)
            {
                foreach (var l in labels)
                {
                    if (line.StartsWith(l, StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
        }
        return true;
    }

    private static string FirstLine(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return line.Length > 120 ? line[..120] : line;
    }
}

/// <summary>高危设备类判定（适配层职责：按设备类名判定，逻辑层不猜）。
/// 显卡/芯片组/主板/存储/网络驱动按高危处理：升级前必须备份且必须用户逐项确认。</summary>
public static class DriverCriticality
{
    private static readonly string[] CriticalKeywords =
    {
        "display", "显卡", "显示适配",
        "chipset", "system", "芯片组", "主板",
        "scsi", "storage", "disk", "存储", "磁盘",
        "net", "网络", "网路",
    };

    public static bool IsCriticalClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return false;
        var lower = className.ToLowerInvariant();
        foreach (var k in CriticalKeywords)
        {
            if (lower.Contains(k)) return true;
        }
        return false;
    }
}
