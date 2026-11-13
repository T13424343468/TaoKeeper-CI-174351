using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules;
using TaoKeeper.AppKernel.ViewModels;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>
/// 相似照片页视图模型测试（R-P10-2b/2c）：建议保留=分辨率最高、组内逐张勾选、
/// 改选保留的互转、确认队列与既有清理链同构、空态如实非白板。
/// </summary>
public class SimilarPhotoPageViewModelTests
{
    private static PhotoFingerprint Fp(string path, long size, int w, int h) =>
        new(path, size, w, h, 0b1010);

    private static SimilarPhotoGroup Group(params PhotoFingerprint[] ps) => new(ps);

    [Fact]
    public void Build_KeepDefaultsToHighestResolution()
    {
        var vm = new SimilarPhotoPageViewModel().BuildFromGroups(new[]
        {
            Group(
                Fp("/p/small.jpg", 100, 100, 100),
                Fp("/p/big.jpg", 500, 400, 300),
                Fp("/p/mid.jpg", 200, 200, 200)),
        });

        var g = Assert.Single(vm.Groups);
        Assert.Equal("/p/big.jpg", g.KeepPath);            // 建议保留=分辨率最高
        Assert.False(g.IsSelected("/p/big.jpg"));          // 保留份不勾
        Assert.True(g.IsSelected("/p/small.jpg"));         // 其余默认勾选拟处理
        Assert.True(g.IsSelected("/p/mid.jpg"));
        Assert.Equal(2, g.SelectedCount);
        Assert.Equal(300, g.SelectedBytes);
        Assert.Equal(800, g.TotalBytes);
    }

    [Fact]
    public void Build_EmptyResult_HonestEmptyState()
    {
        var vm = new SimilarPhotoPageViewModel().BuildFromGroups(Array.Empty<SimilarPhotoGroup>());

        Assert.False(vm.HasGroups);
        Assert.Equal("没找到相似照片", vm.SummaryText);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public void Build_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SimilarPhotoPageViewModel().BuildFromGroups(null!));
    }

    [Fact]
    public void Toggle_KeepPathRefused_UserFileToggles()
    {
        var vm = new SimilarPhotoPageViewModel().BuildFromGroups(new[]
        {
            Group(Fp("/p/a.jpg", 100, 400, 300), Fp("/p/b.jpg", 100, 100, 100)),
        });
        var g = vm.Groups[0];

        Assert.False(g.Toggle("/p/a.jpg"));   // 保留份不可勾
        Assert.True(g.Toggle("/p/b.jpg"));    // 用户文件翻转取消
        Assert.False(g.IsSelected("/p/b.jpg"));
        Assert.True(g.Toggle("/p/b.jpg"));    // 再翻回勾选
        Assert.True(g.IsSelected("/p/b.jpg"));
    }

    [Fact]
    public void SetKeep_MovesSuggestionAndRechecksOldKeep()
    {
        var vm = new SimilarPhotoPageViewModel().BuildFromGroups(new[]
        {
            Group(Fp("/p/a.jpg", 100, 400, 300), Fp("/p/b.jpg", 100, 100, 100)),
        });
        var g = vm.Groups[0];

        Assert.True(g.SetKeep("/p/b.jpg"));   // 改选保留
        Assert.Equal("/p/b.jpg", g.KeepPath);
        Assert.False(g.IsSelected("/p/b.jpg"));
        Assert.True(g.IsSelected("/p/a.jpg"));// 原保留份转为拟处理

        Assert.False(g.SetKeep("/p/ghost.jpg")); // 组外路径不改状态
        Assert.Equal("/p/b.jpg", g.KeepPath);
    }

    [Fact]
    public void BuildCleanupQueue_MirrorsSelectionWithSafetyGate()
    {
        var vm = new SimilarPhotoPageViewModel().BuildFromGroups(new[]
        {
            Group(Fp("/p/keep.jpg", 400, 400, 300), Fp("/p/dup1.jpg", 100, 100, 100), Fp("/p/dup2.jpg", 100, 100, 100)),
        });
        vm.Groups[0].Toggle("/p/dup2.jpg"); // dup2 取消，只处理 dup1

        var queue = vm.BuildCleanupQueue();

        var confirmed = queue.BuildActions();
        var item = Assert.Single(confirmed);
        Assert.Equal("/p/dup1.jpg", item.FullPath);
        Assert.Equal(100, item.SizeBytes);
        Assert.Equal("similar-photo", item.RuleId);
        // 入队口径与重复文件页同构：只有勾选拟处理的张数入队（dup2 已取消、保留份不在队），
        // 受保护路径仍由闸门在入队源头标死，确认闸门归队列管。
        Assert.Equal(1, queue.Items.Count);
    }

    [Fact]
    public void BuildCleanupQueue_NoSelection_EmptyActions()
    {
        var vm = new SimilarPhotoPageViewModel().BuildFromGroups(new[]
        {
            Group(Fp("/p/a.jpg", 100, 200, 200), Fp("/p/b.jpg", 100, 100, 100)),
        });
        vm.Groups[0].Toggle("/p/b.jpg"); // 取消唯一拟处理

        Assert.Empty(vm.BuildCleanupQueue().BuildActions());
    }

    [Fact]
    public void SummaryText_ReflectsCountsAndBytes()
    {
        var vm = new SimilarPhotoPageViewModel().BuildFromGroups(new[]
        {
            Group(Fp("/p/a.jpg", 400, 400, 300), Fp("/p/b.jpg", 100, 100, 100)),
            Group(Fp("/p/c.jpg", 300, 300, 300), Fp("/p/d.jpg", 100, 100, 100)),
        });

        Assert.True(vm.HasGroups);
        Assert.Contains("2 组", vm.SummaryText);
        Assert.Contains("4 张", vm.SummaryText);
    }
}
