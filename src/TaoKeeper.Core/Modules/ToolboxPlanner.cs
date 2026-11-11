using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoKeeper.Core.Modules;

using TaoKeeper.Core.Models;

/// <summary>大文件处置建议项（仅建议，清理动作必须逐条经用户确认后走回收站语义）。</summary>
public sealed record LargeFileItem(
    string FullPath,
    long SizeBytes,
    string Reason);

/// <summary>空文件夹建议项（整棵子树无文件才入列；仅建议，删除动作逐条确认且进回收站）。</summary>
public sealed record EmptyFolderItem(
    string FullPath,
    string Reason);

/// <summary>工具箱编排产出：大文件 Top-N 与空文件夹清单（只读规划结果，不含任何执行动作）。</summary>
public sealed record ToolboxPlan(
    IReadOnlyList<LargeFileItem> LargeFiles,
    IReadOnlyList<EmptyFolderItem> EmptyFolders,
    /// <summary>大文件清单合计体积（字节），即全部处置后可释放的上限。</summary>
    long LargeFilesTotalBytes);

/// <summary>工具箱编排选项。</summary>
public sealed class ToolboxOptions
{
    /// <summary>大文件阈值（字节），默认 512MB；与扫描选项口径一致。</summary>
    public long LargeFileThresholdBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>大文件最多呈现条数（按体积降序取前 N，默认 20）。</summary>
    public int LargeFileTopCount { get; init; } = 20;

    /// <summary>受保护目录名（大小写不敏感）：命中名字的目录及其整棵子树不做任何建议。默认含系统与回收站语义目录。</summary>
    public ISet<string> ProtectedDirectoryNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData",
            "$RECYCLE.BIN", "System Volume Information"
        };

    public static ToolboxOptions Defaults() => new();
}

/// <summary>
/// 工具箱编排（模块 M1 工具箱页后端）：从一次只读扫描结果规划「大文件 Top-N」与「空文件夹」清单。
/// 本类绝不执行任何删除/移动/写入动作；产出仅是带理由的建议项，处置一律逐条经用户确认，
/// 实际清理走回收站语义（由上层清理账本 CleanupLedger 记账）。
/// </summary>
public static class ToolboxPlanner
{
    /// <summary>规划大文件 Top-N 与空文件夹清单。</summary>
    public static ToolboxPlan BuildPlan(ScanResult scan, ToolboxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        options ??= ToolboxOptions.Defaults();

        var largeFiles = scan.Files
            .Where(f => f.SizeBytes >= options.LargeFileThresholdBytes)
            .OrderByDescending(f => f.SizeBytes)
            .Take(Math.Max(0, options.LargeFileTopCount))
            .Select(f => new LargeFileItem(
                f.FullPath, f.SizeBytes,
                $"大文件：{FormatBytes(f.SizeBytes)}，最后修改 {f.LastModifiedUtc.ToLocalTime():yyyy-MM-dd}"))
            .ToList();

        var emptyFolders = FindEmptyFolders(scan, options);

        return new ToolboxPlan(
            largeFiles,
            emptyFolders,
            largeFiles.Sum(f => f.SizeBytes));
    }

    /// <summary>
    /// 空文件夹发现：某目录的整棵子树下没有任何文件（扫描文件清单按路径前缀判定），
    /// 且该目录及其任一祖先不在受保护名单内，才入列。
    /// 目录清单中的最顶层目录（其父目录不在清单内，即用户语义的扫描根）一律不报告——
    /// 删根不是工具箱该替用户决定的事。结果按路径深度降序（深层在前），便于逐层确认。
    /// </summary>
    private static List<EmptyFolderItem> FindEmptyFolders(ScanResult scan, ToolboxOptions options)
    {
        // 文件所在目录集合（归一化：去尾分隔符、统一大小写比较口径）
        var fileDirs = new HashSet<string>(
            scan.Files.Select(f => NormalizePath(f.Directory)),
            StringComparer.OrdinalIgnoreCase);

        var normalizedDirs = scan.Directories
            .Select(d => NormalizePath(d.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<EmptyFolderItem>();
        for (int i = 0; i < scan.Directories.Count; i++)
        {
            var dir = scan.Directories[i];
            var normalized = NormalizePath(dir.FullPath);
            if (IsProtected(normalized, options.ProtectedDirectoryNames)) continue;
            if (IsTopMost(normalized, normalizedDirs)) continue; // 扫描根与其余顶层目录自身不报告

            bool hasFileUnderSubtree = fileDirs.Any(d => IsPathUnder(d, normalized));
            if (hasFileUnderSubtree) continue;

            result.Add(new EmptyFolderItem(
                dir.FullPath,
                dir.FileCount == 0
                    ? "空文件夹：整棵子树无文件"
                    : "空文件夹：子树内仅余空目录"));
        }

        return result
            .OrderByDescending(i => DepthOf(i.FullPath))
            .ThenBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>路径深度：'/' 与 '\\' 都算一层分隔（跨平台口径，Windows 真机与测试数据两种风格都能正确排序）。</summary>
    private static int DepthOf(string path) => path.Count(c => c == '/' || c == '\\');

    /// <summary>父目录不在扫描目录清单内即视为顶层（含盘符根与路径解析到空的情况）。
    /// 手工按两种分隔符找父：System.IO.Path 在非 Windows 上不认 '\'，会把 D:\x 误判为无父目录。</summary>
    private static bool IsTopMost(string normalizedDir, ISet<string> knownDirs)
    {
        int lastSep = Math.Max(normalizedDir.LastIndexOf('/'), normalizedDir.LastIndexOf('\\'));
        if (lastSep < 0) return true;                       // 如 "C:" 或裸名，无父可查
        var parent = normalizedDir[..lastSep];
        if (parent.Length == 0) return true;                // 父即 "/" 根
        return !knownDirs.Contains(parent);
    }

    private static readonly char PathSep = System.IO.Path.DirectorySeparatorChar;

    /// <summary>路径归一化：去尾部分隔符，统一用目录分隔符边界比较。</summary>
    private static string NormalizePath(string path) =>
        string.IsNullOrEmpty(path)
            ? path
            : path.TrimEnd(PathSep, '\\', '/');

    /// <summary>candidate 是否等于 root 或位于 root 之下（按路径段边界，不匹配前缀字符串；'/' 与 '\\' 都识别）。</summary>
    private static bool IsPathUnder(string candidate, string root)
    {
        if (candidate.Length == 0 || root.Length == 0) return false;
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(root + '/', StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>任一路径段命中受保护名单（含祖先）即受保护。</summary>
    private static bool IsProtected(string normalizedPath, ISet<string> protectedNames)
    {
        if (normalizedPath.Length == 0) return false;
        var segments = normalizedPath.Split('/', '\\');
        foreach (var segment in segments)
        {
            if (segment.Length > 0 && protectedNames.Contains(segment)) return true;
        }
        return false;
    }

    /// <summary>字节数转人读体积（B/KB/MB/GB/TB，1 位小数以内）。</summary>
    private static string FormatBytes(long bytes)
    {
        const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
        double v = bytes;
        return bytes >= GB ? $"{v / GB:0.##} GB"
             : bytes >= MB ? $"{v / MB:0.##} MB"
             : bytes >= KB ? $"{v / KB:0.##} KB"
             : $"{bytes} B";
    }
}
