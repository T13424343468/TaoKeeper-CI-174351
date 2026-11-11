using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>重复文件页视图模型测试：分组展示、组内单选保留、确认队列入队与安全闸门衔接。</summary>
public class DuplicateFilesPageViewModelTests
{
    private static FileEntry Fe(string fullPath, long size) =>
        new(fullPath, System.IO.Path.GetFileName(fullPath.Replace('\\', '/')),
            "目录", size, DateTimeOffset.UtcNow, FileAttributesSlim.None);

    private static DuplicateGroup Group(string sha, long size, params string[] paths) =>
        new(sha, size, paths.Select(p => Fe(p, size)).ToList());

    /// <summary>两组：A 组 3 份×100B（可释放 200）、B 组 2 份×300B（可释放 300）。</summary>
    private static List<DuplicateGroup> SampleGroups() => new()
    {
        Group("aaa", 100, @"D:\下载\a1.iso", @"D:\下载\a2.iso", @"E:\备份\a3.iso"),
        Group("bbb", 300, @"D:\照片\b1.bin", @"E:\照片\b2.bin"),
    };

    [Fact]
    public void 构建分组_按可释放体积降序_默认保留第一份()
    {
        var vm = new DuplicateFilesPageViewModel().BuildFromGroups(SampleGroups());

        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal("bbb", vm.Groups[0].Sha256); // 300 > 200，大组在前
        Assert.Equal("aaa", vm.Groups[1].Sha256);

        var b = vm.Groups[0];
        Assert.Equal(@"D:\照片\b1.bin", b.KeepPath);           // 默认保留组内第一份
        var pending = Assert.Single(b.PendingPaths);
        Assert.Equal(@"E:\照片\b2.bin", pending);
        Assert.Equal(300, b.ReclaimableBytes);

        Assert.Equal(200, vm.Groups[1].ReclaimableBytes);
        Assert.Equal(5, vm.Groups.Sum(g => g.FullPaths.Count));
        Assert.Equal(3, vm.PendingCount); // (3-1) + (2-1)，每组各留一份
        Assert.Equal(500, vm.TotalReclaimableBytes);
        Assert.True(vm.HasDuplicates);
        Assert.Contains("2 组重复文件", vm.SummaryText);
        Assert.Contains("500 B", vm.SummaryText);
    }

    [Fact]
    public void 改选保留份_组内单选互斥_可释放量不变()
    {
        var vm = new DuplicateFilesPageViewModel().BuildFromGroups(SampleGroups());
        var a = vm.Groups[1];

        Assert.True(a.TrySetKeep(@"E:\备份\a3.iso"));

        Assert.Equal(@"E:\备份\a3.iso", a.KeepPath);
        Assert.Equal(2, a.PendingPaths.Count);
        Assert.Contains(@"D:\下载\a1.iso", a.PendingPaths);
        Assert.Contains(@"D:\下载\a2.iso", a.PendingPaths);
        Assert.DoesNotContain(@"E:\备份\a3.iso", a.PendingPaths);
        Assert.Equal(200, a.ReclaimableBytes);   // 恒保留一份，本组可释放量不随保留位变化
        Assert.Equal(500, vm.TotalReclaimableBytes); // 每组恒保留一份，合计可释放量不变
    }

    [Fact]
    public void 改选当前保留份或组外路径_返回假且状态不变()
    {
        var vm = new DuplicateFilesPageViewModel().BuildFromGroups(SampleGroups());
        var b = vm.Groups[0];

        Assert.False(b.TrySetKeep(b.KeepPath));                    // 已是保留份
        Assert.False(b.TrySetKeep(@"C:\不存在\x.bin"));            // 组外路径
        Assert.Single(b.PendingPaths);
        Assert.Equal(300, b.ReclaimableBytes);
    }

    [Fact]
    public void 文件数不足二的组跳过_空清单走空态()
    {
        var single = new List<DuplicateGroup>
        {
            Group("solo", 100, @"D:\x\only.bin"),  // 1 份，无可清理
        };
        var vm = new DuplicateFilesPageViewModel().BuildFromGroups(single);
        Assert.False(vm.HasDuplicates);
        Assert.Equal("未发现重复文件", vm.SummaryText);
        Assert.Empty(vm.BuildCleanupQueue().Items);

        var empty = new DuplicateFilesPageViewModel().BuildFromGroups(new List<DuplicateGroup>());
        Assert.Equal("未发现重复文件", empty.SummaryText);
    }

    [Fact]
    public void 待清理入统一确认队列_受保护路径源头标死()
    {
        var groups = new List<DuplicateGroup>
        {
            Group("win", 100, @"D:\x\w1.bin", @"C:\Windows\w2.bin"), // 待清理份落在受保护路径
            Group("usr", 200, @"D:\u1.bin", @"D:\u2.bin"),
        };
        var vm = new DuplicateFilesPageViewModel().BuildFromGroups(groups);
        var queue = vm.BuildCleanupQueue();

        Assert.Equal(2, queue.Items.Count); // 每组保留一份：win 组 1 份待清理 + usr 组 1 份待清理

        var protectedItem = queue.Items.Single(i => i.FullPath == @"C:\Windows\w2.bin");
        Assert.NotNull(protectedItem.PathRefusal);   // 受保护路径从源头标死
        Assert.False(protectedItem.CanConfirm);

        queue.SelectAll();
        var actions = queue.BuildActions();
        Assert.Single(actions);                    // 受保护项勾不上，绝不进执行清单
        Assert.Equal(@"D:\u2.bin", actions[0].FullPath);
        Assert.Equal("duplicate", actions[0].Source);
        Assert.Equal("duplicate-file", actions[0].RuleId);
    }

    [Fact]
    public void 入参为空_报红不静默()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DuplicateFilesPageViewModel().BuildFromGroups(null));
    }

    [Fact]
    public void 组条目主题色_金随主题即时取值()
    {
        var vm = new DuplicateFilesPageViewModel().BuildFromGroups(SampleGroups());
        Assert.Equal(ThemeTokens.Shuimo.Gold, vm.GroupColorOf(ThemeTokens.Shuimo));
        Assert.Equal(ThemeTokens.Mobai.Gold, vm.GroupColorOf(ThemeTokens.Mobai));
    }
}
