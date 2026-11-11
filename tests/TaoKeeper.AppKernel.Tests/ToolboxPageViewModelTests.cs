using System;
using System.Collections.Generic;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>工具箱页视图模型测试：计划→确认队列接线、分区归属、概览文本、主题色。</summary>
public class ToolboxPageViewModelTests
{
    private static FileEntry Fe(string fullPath, long size)
    {
        // 手工按两种分隔符取父目录：System.IO.Path 在非 Windows 上不认 '\'（跨平台坑，沙箱测试同样要绕）
        var path = fullPath.TrimEnd('\\', '/');
        int lastSep = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        var dir = lastSep > 0 ? path[..lastSep] : path;
        return new(fullPath, System.IO.Path.GetFileName(fullPath.Replace('\\', '/')),
            dir, size, DateTimeOffset.UtcNow, FileAttributesSlim.None);
    }

    private static DirectoryStats Ds(string fullPath) => new(fullPath, 0, 0);

    private static ScanResult Scan(IReadOnlyList<FileEntry> files, IReadOnlyList<DirectoryStats> dirs) =>
        new(ScanMode.Quick, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            files, dirs, Array.Empty<FileEntry>(), files.Sum(f => f.SizeBytes), files.Count);

    [Fact]
    public void 计划接入确认队列_大文件与空文件夹合并入队()
    {
        var files = new List<FileEntry>
        {
            Fe(@"D:\a\big.iso", 600L * 1024 * 1024),
            Fe(@"D:\a\b\mid.bin", 550L * 1024 * 1024),       // 高于阈值 512MB，入列
        };
        var dirs = new List<DirectoryStats> { Ds(@"D:\"), Ds(@"D:\a"), Ds(@"D:\a\b"), Ds(@"D:\空目录") };

        var vm = new ToolboxPageViewModel().BuildFromScan(Scan(files, dirs),
            new ToolboxOptions { LargeFileThresholdBytes = 512L * 1024 * 1024, LargeFileTopCount = 10 });

        Assert.True(vm.HasSuggestions);
        Assert.NotNull(vm.Queue);
        // 队列条目：2 个大文件 + 1 个空目录（目录 a、a\b 有文件不入列，根 D:\ 顶层不报）
        Assert.Equal(3, vm.Queue!.Items.Count);
        Assert.All(vm.Queue.Items, i => Assert.False(i.Confirmed));    // 默认全未勾选
        Assert.Equal(3, vm.Sections.Count);                            // 2 大文件 + 1 空目录归属
        Assert.Equal(ToolboxSection.LargeFiles, vm.Sections[@"D:\a\big.iso"]);
        Assert.Equal(ToolboxSection.EmptyFolders, vm.Sections[@"D:\空目录"]);
    }

    [Fact]
    public void 概览文本_含计数与合计()
    {
        var files = new List<FileEntry> { Fe(@"D:\big.iso", 2L * 1024 * 1024 * 1024) };
        var vm = new ToolboxPageViewModel().BuildFromScan(
            Scan(files, new List<DirectoryStats> { Ds(@"D:\") }),
            new ToolboxOptions { LargeFileThresholdBytes = 512L * 1024 * 1024, LargeFileTopCount = 10 });

        Assert.Contains("1 项", vm.SummaryText);
        Assert.Contains("GB", vm.SummaryText);
        Assert.True(vm.HasLargeFiles);
        Assert.False(vm.HasEmptyFolders);
    }

    [Fact]
    public void 扫描很干净_无建议且队列可直接执行态()
    {
        var vm = new ToolboxPageViewModel().BuildFromScan(
            Scan(Array.Empty<FileEntry>(), new List<DirectoryStats> { Ds(@"D:\") }));

        Assert.False(vm.HasSuggestions);
        Assert.Empty(vm.Queue!.Items);
        Assert.False(vm.HasLargeFiles);
        Assert.False(vm.HasEmptyFolders);
    }

    [Fact]
    public void 空文件夹条目_体积按0记_勾选后进入执行清单()
    {
        var vm = new ToolboxPageViewModel().BuildFromScan(
            Scan(Array.Empty<FileEntry>(), new List<DirectoryStats> { Ds(@"D:\"), Ds(@"D:\空目录") }));

        Assert.True(vm.Queue!.Toggle(@"D:\空目录"));
        var actions = vm.Queue.BuildActions();
        var item = Assert.Single(actions);
        Assert.Equal(0, item.SizeBytes);                        // 删空目录不释放文件体积
        Assert.Equal("empty-folder", item.RuleId);
        Assert.True(item.Confirmed);
    }

    [Fact]
    public void 分区色随三主题即时取值()
    {
        var vm = new ToolboxPageViewModel();
        foreach (var theme in new[] { ThemeName.Shuimo, ThemeName.Tianqing, ThemeName.Mobai })
        {
            var tokens = ThemeTokens.Of(theme);
            Assert.Equal(tokens.Gold, vm.SectionColorOf(ToolboxSection.LargeFiles, tokens));
            Assert.Equal(tokens.Azure, vm.SectionColorOf(ToolboxSection.EmptyFolders, tokens));
        }
    }
}
