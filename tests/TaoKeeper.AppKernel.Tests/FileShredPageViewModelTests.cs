using System;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>文件粉碎页视图模型测试：状态机相位、拒绝重入、未接线如实失败、回执数字如实、计划汇总文案。</summary>
public class FileShredPageViewModelTests
{
    private static ShredRunResult Result(int count, long bytes) =>
        new(count, bytes, Array.Empty<ShredOutcome>());

    private static ShredItem AllowedItem(long size) => new(@"C:\t\a.bin", size, Allowed: true);

    private static ShredItem DeniedItem(string reason) => new(@"C:\Windows\t\b.bin", 0, Allowed: false, reason);

    [Fact]
    public void 初始态_Idle且汇总为空态()
    {
        var vm = new FileShredPageViewModel();
        Assert.Equal(FileShredRunPhase.Idle, vm.RunPhase);
        Assert.Equal("尚未选择文件", vm.PlanSummaryText);
        Assert.Null(vm.FailureMessage);
        Assert.Equal(0, vm.ShreddedCount);
    }

    [Fact]
    public async Task 执行_成功完成回执数字如实()
    {
        var vm = new FileShredPageViewModel();
        await vm.RunAsync(_ => Task.FromResult(Result(2, 4096)));
        Assert.Equal(FileShredRunPhase.Completed, vm.RunPhase);
        Assert.Equal(2, vm.ShreddedCount);
        Assert.Equal(4096, vm.FreedBytes);
        Assert.Contains("2 个文件", vm.ReceiptText);
        Assert.Contains("4 KB", vm.ReceiptText);
        Assert.Null(vm.FailureMessage);
    }

    [Fact]
    public async Task 执行_取消转失败且原因如实()
    {
        var vm = new FileShredPageViewModel();
        await vm.RunAsync(_ => Task.FromCanceled<ShredRunResult>(new CancellationToken(true)));
        Assert.Equal(FileShredRunPhase.Failed, vm.RunPhase);
        Assert.Contains("取消", vm.FailureMessage);
        Assert.Contains("原状", vm.FailureMessage);
    }

    [Fact]
    public async Task 执行_执行器抛错转失败且原因如实()
    {
        var vm = new FileShredPageViewModel();
        await vm.RunAsync(_ => Task.FromException<ShredRunResult>(new InvalidOperationException("盘已满")));
        Assert.Equal(FileShredRunPhase.Failed, vm.RunPhase);
        Assert.Contains("盘已满", vm.FailureMessage);
    }

    [Fact]
    public async Task 执行_未接线如实失败不假碎()
    {
        var vm = new FileShredPageViewModel();
        await vm.RunAsync(null);
        Assert.Equal(FileShredRunPhase.Failed, vm.RunPhase);
        Assert.Contains("尚未接线", vm.FailureMessage);
    }

    [Fact]
    public async Task 执行_Running拒绝重入()
    {
        var vm = new FileShredPageViewModel();
        var gate = new TaskCompletionSource();
        var run = vm.RunAsync(async _ =>
        {
            await gate.Task;
            return Result(0, 0);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vm.RunAsync(_ => Task.FromResult(Result(1, 1))));
        gate.SetResult();
        await run;
        Assert.Equal(FileShredRunPhase.Completed, vm.RunPhase);
    }

    [Fact]
    public async Task 重跑_失败后可重跑且失败原因清空()
    {
        var vm = new FileShredPageViewModel();
        await vm.RunAsync(null);
        Assert.Equal(FileShredRunPhase.Failed, vm.RunPhase);
        await vm.RunAsync(_ => Task.FromResult(Result(1, 128)));
        Assert.Equal(FileShredRunPhase.Completed, vm.RunPhase);
        Assert.Null(vm.FailureMessage);
        Assert.Equal(1, vm.ShreddedCount);
    }

    [Fact]
    public void 计划汇总_可粉碎与不可选分别如实()
    {
        var summary = FileShredPageViewModel.BuildPlanSummary(new[]
        {
            AllowedItem(1024), AllowedItem(2048), DeniedItem("系统关键目录内的文件不可粉碎（N6）"),
        });
        Assert.Contains("2 项可粉碎", summary);
        Assert.Contains("3 KB", summary);
        Assert.Contains("1 项不可选", summary);
    }

    [Fact]
    public void 计划汇总_全不可选与空清单如实()
    {
        Assert.Contains("没有可粉碎的文件",
            FileShredPageViewModel.BuildPlanSummary(new[] { DeniedItem("x") }));
        Assert.Equal("尚未选择文件", FileShredPageViewModel.BuildPlanSummary(Array.Empty<ShredItem>()));
    }

    [Fact]
    public void 相位变化回调_按序触发()
    {
        var vm = new FileShredPageViewModel();
        var phases = new System.Collections.Generic.List<FileShredRunPhase>();
        vm.PhaseChanged = phases.Add;
        vm.RunAsync(_ => Task.FromResult(Result(1, 1))).GetAwaiter().GetResult();
        Assert.Equal(new[] { FileShredRunPhase.Running, FileShredRunPhase.Completed }, phases);
    }
}
