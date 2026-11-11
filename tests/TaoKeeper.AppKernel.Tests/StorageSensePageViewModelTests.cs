using System;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>存储感知页视图模型测试：默认关、逐项勾选边界、到点确认预览、执行回填记账。</summary>
public class StorageSensePageViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 默认关闭_状态文案不装傻()
    {
        var vm = new StorageSensePageViewModel();
        Assert.False(vm.Plan.Enabled);
        Assert.Contains("已关闭", vm.GetStatusText(Now));
        Assert.False(vm.EvaluateDue(Now, 10, 100));
        Assert.Null(vm.PendingPreview);
    }

    [Fact]
    public void 开启校验范围_逐项勾选_清空报红()
    {
        var vm = new StorageSensePageViewModel();
        Assert.Throws<ArgumentException>(() => vm.Enable(false, Now));   // 一个范围都没勾

        vm.ToggleScope(StorageCleanupScope.TempFiles, true);
        vm.ToggleScope(StorageCleanupScope.BrowserCache, true);
        vm.Enable(true, Now);
        Assert.True(vm.Plan.Enabled);
        Assert.Contains("临时文件", vm.ScopeDisplay);
        Assert.Contains("浏览器缓存", vm.ScopeDisplay);

        vm.ToggleScope(StorageCleanupScope.BrowserCache, false);
        Assert.Throws<InvalidOperationException>(() =>              // 开启状态下清空最后一项 → 报红引导先关总开关
            vm.ToggleScope(StorageCleanupScope.TempFiles, false));
        Assert.Contains("临时文件", vm.ScopeDisplay);               // 勾选未被清掉
        vm.Disable();                                                // 先关总开关
        vm.ToggleScope(StorageCleanupScope.TempFiles, false);        // 关闭后允许清空
        Assert.Equal(StorageCleanupScope.None, vm.Plan.Scope);
    }

    [Fact]
    public void 到点产出确认预览_未到点静默_确认文案写明回收站()
    {
        var vm = new StorageSensePageViewModel();
        vm.ToggleScope(StorageCleanupScope.TempFiles, true);
        vm.SetIntervalDays(7);
        vm.Enable(false, Now);

        Assert.False(vm.EvaluateDue(Now.AddDays(6), 10, 100));           // 未到点
        Assert.True(vm.EvaluateDue(Now.AddDays(7), 10, 100));            // 到点
        Assert.NotNull(vm.PendingPreview);
        Assert.Contains("回收站", vm.PendingPreview!.ConfirmText);
        Assert.Contains("待确认", vm.GetStatusText(Now.AddDays(7)));
    }

    [Fact]
    public void 执行回填记账_锚点顺延_待确认清除()
    {
        var vm = new StorageSensePageViewModel();
        vm.ToggleScope(StorageCleanupScope.TempFiles, true);
        vm.Enable(false, Now);
        Assert.True(vm.EvaluateDue(Now.AddDays(7), 10, 100));

        var runAt = Now.AddDays(7).AddHours(1);
        vm.MarkRunCompleted(runAt);
        Assert.Equal(runAt, vm.Plan.LastRunUtc);
        Assert.Null(vm.PendingPreview);
        Assert.False(vm.EvaluateDue(runAt.AddDays(1), 10, 100));         // 新锚点未到
        Assert.True(vm.EvaluateDue(runAt.AddDays(7), 10, 100));          // 到点再出预览
    }

    [Fact]
    public void 关闭保留勾选_清除待确认_间隔最小1天()
    {
        var vm = new StorageSensePageViewModel();
        vm.ToggleScope(StorageCleanupScope.DownloadsAged, true);
        vm.SetIntervalDays(0);
        vm.Enable(false, Now);
        vm.Disable();
        Assert.False(vm.Plan.Enabled);
        Assert.False(string.IsNullOrEmpty(vm.ScopeDisplay));             // 勾选保留
        Assert.Equal(1, vm.Plan.IntervalDays);
        Assert.Contains("已关闭", vm.GetStatusText(Now));
    }
}
