using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// 工具箱编排测试：大文件 Top-N、空文件夹发现（子树无文件口径、受保护目录与祖先豁免）。
/// 只读编排铁律：计划零执行动作，任何清理都由用户逐条确认后走回收站语义。
/// 注意：测试数据用 '/' 风格合成路径；目录字段必须手工解析（'/' 与 '\' 都识别），
/// 不得用 System.IO.Path.GetDirectoryName——Windows 会把 "/d/x" 解析成 "\d\x"，与目录清单口径错位。
/// </summary>
public class ToolboxPlannerTests
{
    /// <summary>手工取文件所在目录（跨平台口径）：截到最后一个 '/' 或 '\' 之前，找不到分隔符为空串。</summary>
    private static string DirOf(string fullPath)
    {
        var lastSep = fullPath.LastIndexOfAny(new[] { '/', '\\' });
        return lastSep <= 0 ? string.Empty : fullPath[..lastSep];
    }

    /// <summary>手工取文件名（跨平台口径）：取最后一个 '/' 或 '\' 之后的部分。</summary>
    private static string NameOf(string fullPath) =>
        fullPath[(fullPath.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];

    private static FileEntry Fe(string fullPath, long size, string? dir = null) =>
        new(fullPath, NameOf(fullPath), dir ?? DirOf(fullPath),
            size, DateTimeOffset.UtcNow, FileAttributesSlim.None);

    private static DirectoryStats Ds(string fullPath, long bytes = 0, int files = 0) =>
        new(fullPath, bytes, files);

    private static ScanResult Scan(
        IReadOnlyList<FileEntry> files, IReadOnlyList<DirectoryStats> dirs) =>
        new(ScanMode.Quick, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            files, dirs, Array.Empty<FileEntry>(), files.Sum(f => f.SizeBytes), files.Count);

    [Fact]
    public void 大文件_按阈值过滤并降序取TopN()
    {
        var files = new List<FileEntry>
        {
            Fe("/d/a.iso", 900 * 1024 * 1024),                     // 900MB 入列
            Fe("/d/b.bin", 100 * 1024 * 1024),                     // 低于默认阈值，不入列
            Fe("/d/c.vhdx", 3L * 1024 * 1024 * 1024),              // 3GB 入列
            Fe("/d/sub/d.mid", 800 * 1024 * 1024),                 // 子目录文件入列
        };
        var scan = Scan(files, new List<DirectoryStats>());

        var plan = ToolboxPlanner.BuildPlan(scan, new ToolboxOptions { LargeFileTopCount = 2 });

        Assert.Equal(2, plan.LargeFiles.Count);                       // Top-N 截断
        Assert.Equal("/d/c.vhdx", plan.LargeFiles[0].FullPath);        // 降序
        Assert.Equal("/d/a.iso", plan.LargeFiles[1].FullPath);
        Assert.Equal(3L * 1024 * 1024 * 1024 + 900L * 1024 * 1024,
            plan.LargeFilesTotalBytes);                               // 合计=Top-N 内各项和
        Assert.All(plan.LargeFiles, f => Assert.StartsWith("大文件：", f.Reason));
    }

    [Fact]
    public void 空文件夹_整棵子树无文件才入列且深层在前()
    {
        // 布局：/d/empty（子树全空）嵌套 /d/empty/inner/leaf 三层；
        // /d/half 自身无文件但子目录有文件（不入列）；/d/full 直属文件（不入列）。
        var files = new List<FileEntry>
        {
            Fe("/d/full/f.txt", 10),
            Fe("/d/half/sub/g.txt", 20),
        };
        var dirs = new List<DirectoryStats>
        {
            Ds("/d"), Ds("/d/empty"), Ds("/d/empty/inner"), Ds("/d/empty/inner/leaf"),
            Ds("/d/half"), Ds("/d/half/sub"), Ds("/d/full"),
        };
        var plan = ToolboxPlanner.BuildPlan(Scan(files, dirs));

        var paths = plan.EmptyFolders.Select(i => i.FullPath).ToList();
        Assert.Equal(3, paths.Count);
        Assert.Equal(new[] { "/d/empty/inner/leaf", "/d/empty/inner", "/d/empty" },
            paths);                                                       // 深层在前，父链整串入列
    }

    [Fact]
    public void 空文件夹_反斜杠路径口径与正斜杠同样正确()
    {
        // 真机回归看护（2026-11-09）：同一布局用 '\' 风格路径，且文件目录字段用显式反斜杠值
        // 模拟 Windows 实测口径——带文件目录必须正确归属，误判成空文件夹即红。
        var files = new List<FileEntry>
        {
            new(@"\d\full\f.txt", "f.txt", @"\d\full", 10,
                DateTimeOffset.UtcNow, FileAttributesSlim.None),
            new(@"\d\half\sub\g.txt", "g.txt", @"\d\half\sub", 20,
                DateTimeOffset.UtcNow, FileAttributesSlim.None),
        };
        var dirs = new List<DirectoryStats>
        {
            Ds(@"\d"), Ds(@"\d\empty"), Ds(@"\d\empty\inner"), Ds(@"\d\empty\inner\leaf"),
            Ds(@"\d\half"), Ds(@"\d\half\sub"), Ds(@"\d\full"),
        };
        var plan = ToolboxPlanner.BuildPlan(Scan(files, dirs));

        var paths = plan.EmptyFolders.Select(i => i.FullPath).ToList();
        Assert.Equal(new[] { @"\d\empty\inner\leaf", @"\d\empty\inner", @"\d\empty" },
            paths);                                                       // 只空子树入列，深层在前
    }

    [Fact]
    public void 空文件夹_受保护目录及其子树一律豁免()
    {
        // Windows 目录即使整棵子树无文件也不建议；其下任何路径同样豁免。
        var dirs = new List<DirectoryStats>
        {
            Ds("/d/Windows/Temp/空"), Ds("/d/常规空目录"), Ds("/d"),
        };
        var plan = ToolboxPlanner.BuildPlan(Scan(Array.Empty<FileEntry>(), dirs));

        var paths = plan.EmptyFolders.Select(i => i.FullPath).ToList();
        Assert.Equal(new[] { "/d/常规空目录" }, paths);                // 仅常规目录入列
        Assert.DoesNotContain(plan.EmptyFolders, i => i.FullPath.Contains("Windows"));
    }

    [Fact]
    public void 空文件夹_扫描根目录自身不报告()
    {
        var dirs = new List<DirectoryStats> { Ds("/d"), Ds("/d/空") };
        var plan = ToolboxPlanner.BuildPlan(Scan(Array.Empty<FileEntry>(), dirs));
        Assert.DoesNotContain(plan.EmptyFolders, i => i.FullPath == "/d");
        Assert.Contains(plan.EmptyFolders, i => i.FullPath == "/d/空");
    }

    [Fact]
    public void 空文件夹_受保护判定按路径段而非字符串前缀()
    {
        // 目录名「WindowsUpdate 备份」含 Windows 字样但不是受保护段，应正常入列。
        var dirs = new List<DirectoryStats> { Ds("/d"), Ds("/d/WindowsUpdate 备份") };
        var plan = ToolboxPlanner.BuildPlan(Scan(Array.Empty<FileEntry>(), dirs));
        Assert.Contains(plan.EmptyFolders, i => i.FullPath == "/d/WindowsUpdate 备份");
    }
}
