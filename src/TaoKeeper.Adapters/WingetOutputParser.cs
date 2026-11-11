using System;
using System.Collections.Generic;
using System.Text;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>
/// winget 表格输出解析器（纯逻辑，可沙箱测试）。
/// winget list / upgrade 的表格按「表头列偏移」切列，系统语言会本地化表头
/// （英文 Name/Id/Version/Available/Source，中文 名称/ID/版本/可用/源），两种都要认。
/// 控制台按「显示格」对齐：中文等全角字符占 2 格，所以列切分按显示宽度计算，不按字符数。
/// 表头无法识别时抛 FormatException 报红——绝不把「认不出的输出」当成空清单（假空比报错更危险）。
/// 长值会被 winget 截断省略号；本层如实保留截断文本，不做猜测性补全。
/// </summary>
public static class WingetOutputParser
{
    /// <summary>一条解析结果：winget 的 Id、名称、现版、可升级版（null=无/未知）。</summary>
    public sealed record WingetRow(string Id, string Name, string? InstalledVersion, string? AvailableVersion);

    /// <summary>列定义：规范键 → 各语言表头候选（不区分大小写）。</summary>
    private static readonly (string Key, string[] Headers)[] KnownColumns =
    {
        ("Name", new[] { "Name", "名称" }),
        ("Id", new[] { "Id", "ID" }),
        ("Version", new[] { "Version", "版本" }),
        ("Available", new[] { "Available", "可用" }),
        ("Source", new[] { "Source", "源" }),
    };

    /// <summary>解析 winget upgrade 输出：Version 列为现版，Available 列为新版。</summary>
    public static IReadOnlyList<SoftwareEntry> ParseUpgrades(string output)
    {
        var (rows, headerFound) = ParseRows(output);
        RequireHeader(output, headerFound, "upgrade");
        var result = new List<SoftwareEntry>(rows.Count);
        foreach (var r in rows)
        {
            result.Add(new SoftwareEntry(
                r.Id, r.Name, Publisher: null,
                InstalledVersion: r.InstalledVersion,
                AvailableVersion: r.AvailableVersion,
                SoftwareSourceKind.WingetCommunity,
                HashVerified: null,
                SourceDisplay: "winget 社区源"));
        }
        return result;
    }

    /// <summary>解析 winget list 输出：Version 列为现版；Available 有值且非未知时一并带回。</summary>
    public static IReadOnlyList<SoftwareEntry> ParseList(string output)
    {
        var (rows, headerFound) = ParseRows(output);
        RequireHeader(output, headerFound, "list");
        var result = new List<SoftwareEntry>(rows.Count);
        foreach (var r in rows)
        {
            // list 输出里 Available 空白/占位表示无升级信息，统一回 null，升级判定以 winget upgrade 为准
            var available = IsPlaceholder(r.AvailableVersion) ? null : r.AvailableVersion;
            result.Add(new SoftwareEntry(
                r.Id, r.Name, Publisher: null,
                InstalledVersion: r.InstalledVersion,
                AvailableVersion: available,
                SoftwareSourceKind.WingetCommunity,
                HashVerified: null,
                SourceDisplay: "winget 社区源"));
        }
        return result;
    }

    private static void RequireHeader(string output, bool headerFound, string command)
    {
        if (headerFound) return;
        // 完全空输出=合法「无内容」；有文本却认不出表头=本地化/版本差异，必须报红而不是当空清单
        if (output.AsSpan().Trim().Length > 0)
            throw new FormatException(
                $"winget {command} 输出表头未识别（可能是未覆盖的本地化表头或版本差异），已拒绝按空清单处理；请回报该输出原文以扩展解析器");
    }

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value is "Unknown" or "unknown" or "未知" or "无" or "-" or "…" or "...";

    /// <summary>显示格宽度：全角/CJK 记 2，其余记 1（控制台对齐口径）。</summary>
    public static int DisplayWidth(string text)
    {
        var w = 0;
        foreach (var c in text) w += IsWide(c) ? 2 : 1;
        return w;
    }

    internal static bool IsWide(char c) =>
        (c >= '\u1100' && c <= '\u115f')
        || (c >= '\u2e80' && c <= '\ua4cf')
        || (c >= '\uac00' && c <= '\ud7a3')
        || (c >= '\uf900' && c <= '\ufaff')
        || (c >= '\ufe30' && c <= '\ufe4f')
        || (c >= '\uff00' && c <= '\uff60')
        || (c >= '\uffe0' && c <= '\uffe6');

    /// <summary>
    /// 按表头列显示格偏移解析全部数据行。
    /// 空行/分隔线跳过；Id 缺失且带文本的行视为上一行的换行续文，追加到上一行名称。
    /// 返回行清单与「表头是否识别成功」。
    /// </summary>
    public static (IReadOnlyList<WingetRow> Rows, bool HeaderFound) ParseRows(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var rows = new List<WingetRow>();
        int[] starts = Array.Empty<int>();
        string[] keys = Array.Empty<string>();
        bool headerFound = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!headerFound)
            {
                if (!TryLocateHeader(line, out var headerKeys, out var headerStarts)) continue;
                keys = headerKeys;
                starts = headerStarts;
                headerFound = true;
                continue;
            }

            if (IsSeparator(line)) continue;

            if (TryParseRow(line, starts, keys, out var row))
            {
                rows.Add(row);
            }
            else if (rows.Count > 0)
            {
                // 续文行：winget 对过长名称会折行，折行行在 Id 列偏移处为空白
                var text = line.Trim();
                if (text.Length > 0)
                    rows[^1] = rows[^1] with { Name = rows[^1].Name + text };
            }
        }
        return (rows, headerFound);
    }

    private static bool IsSeparator(string line)
    {
        foreach (var c in line)
        {
            if (c != '-' && c != ' ' && c != '=' && c != '\t') return false;
        }
        return line.Length > 0;
    }

    private static bool TryLocateHeader(string line, out string[] keys, out int[] starts)
    {
        keys = Array.Empty<string>();
        starts = Array.Empty<int>();
        var found = new List<(string Key, int CellPos)>();

        foreach (var (key, headers) in KnownColumns)
        {
            int bestCharIndex = -1;
            foreach (var h in headers)
            {
                var idx = line.IndexOf(h, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (bestCharIndex < 0 || idx < bestCharIndex)) bestCharIndex = idx;
            }
            if (bestCharIndex < 0)
            {
                if (key == "Available") continue; // 部分输出（如旧版 winget list）没有可用列
                return false;                     // Name/Id/Version/Source 是必需列，缺一不成表头
            }
            found.Add((key, DisplayWidth(line[..bestCharIndex])));
        }

        found.Sort((a, b) => a.CellPos.CompareTo(b.CellPos));
        keys = new string[found.Count];
        starts = new int[found.Count];
        for (int i = 0; i < found.Count; i++)
        {
            keys[i] = found[i].Key;
            starts[i] = found[i].CellPos;
        }
        return true;
    }

    /// <summary>按显示格区间取列值：取完全落在 [startCell, endCell) 内的字符（跨界全角字符不取）。</summary>
    private static string SliceByCells(string line, int startCell, int endCellExclusive)
    {
        var sb = new StringBuilder();
        var cell = 0;
        foreach (var c in line)
        {
            var w = IsWide(c) ? 2 : 1;
            if (cell >= startCell && cell + w <= endCellExclusive) sb.Append(c);
            cell += w;
            if (cell >= endCellExclusive) break;
        }
        return sb.ToString();
    }

    private static bool TryParseRow(string line, int[] starts, string[] keys, out WingetRow row)
    {
        row = new WingetRow(string.Empty, string.Empty, null, null);
        if (string.IsNullOrWhiteSpace(line)) return false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        // 长名称溢出会让后续列整体右移，表头列偏移失效；此时名称列被写满且无尾部空白，
        // 改按空白游程（≥2 连续空格）切分对位，仍要求段数与列数一致才采用，否则维持原切法。
        if (NameRegionFull(line, starts.Length > 1 ? starts[1] : int.MaxValue))
        {
            var tokens = SplitByWhitespaceRuns(line);
            if (tokens.Count == keys.Length)
            {
                for (int i = 0; i < keys.Length; i++) values[keys[i]] = tokens[i].Trim();
            }
        }
        if (values.Count == 0)
        {
            for (int i = 0; i < starts.Length; i++)
            {
                int begin = starts[i];
                int end = i + 1 < starts.Length ? starts[i + 1] : int.MaxValue;
                values[keys[i]] = SliceByCells(line, begin, end).Trim();
            }
        }

        var id = values.GetValueOrDefault("Id", string.Empty);
        var name = values.GetValueOrDefault("Name", string.Empty);
        if (string.IsNullOrEmpty(id)) return false;

        var version = IsPlaceholder(values.GetValueOrDefault("Version")) ? null : values.GetValueOrDefault("Version");
        var available = values.TryGetValue("Available", out var av) && !IsPlaceholder(av) ? av : null;

        row = new WingetRow(id, name, version, available);
        return true;
    }

    /// <summary>名称列区间内没有任何空白（被长名称写满）= 列整体右移，按列偏移切分已失效。</summary>
    private static bool NameRegionFull(string line, int endCellExclusive)
    {
        var cell = 0;
        foreach (var c in line)
        {
            if (cell >= endCellExclusive) return true;
            if (char.IsWhiteSpace(c)) return false;
            cell += IsWide(c) ? 2 : 1;
        }
        return cell >= endCellExclusive;
    }

    /// <summary>按空白游程（≥2 个连续空白）切列；单个空格视为词内空格保留。全空白行返回空表。</summary>
    private static List<string> SplitByWhitespaceRuns(string line)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var run = 0;
        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c)) { run++; continue; }
            if (run >= 2 && sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            else if (run == 1) sb.Append(' ');
            run = 0;
            sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }
}
