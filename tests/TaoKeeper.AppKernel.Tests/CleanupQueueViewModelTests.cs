using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.AppKernel.ViewModels;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>
/// 清理确认队列视图模型测试：路径级预检拒绝不可勾选、逐条/批量勾选、
/// BuildActions 只出确认项、零静默删除（未确认不进执行清单）。
/// </summary>
public class CleanupQueueViewModelTests
{
    private static (string, long, string, string?) It(string path, long size = 100,
        string source = "junk") => (path, size, source, null);

    [Fact]
    public void 受保护与根路径条目_不可勾选且给出预检理由()
    {
        var vm = new CleanupQueueViewModel(new[]
        {
            It(@"C:\Windows\Temp\x.log"),
            It(@"C:\"),
            It(@"D:\下载\临时.log"),
        });

        Assert.False(vm.Items[0].CanConfirm);                     // 受保护段：源头标死
        Assert.Contains("受保护路径", vm.Items[0].PathRefusal);
        Assert.False(vm.Items[1].CanConfirm);                     // 盘符根
        Assert.Contains("盘符根", vm.Items[1].PathRefusal);
        Assert.True(vm.Items[2].CanConfirm);                      // 正常条目

        vm.SelectAll();                                           // 全选也勾不上拒绝项
        Assert.False(vm.Items[0].Confirmed);
        Assert.False(vm.Items[1].Confirmed);
        Assert.True(vm.Items[2].Confirmed);
    }

    [Fact]
    public void 拒绝项直接赋真_状态保持未确认()
    {
        var vm = new CleanupQueueViewModel(new[] { It(@"C:\ProgramData\y.dat") });
        vm.Items[0].Confirmed = true;                             // 零静默删除：赋值被拒不抛异常
        Assert.False(vm.Items[0].Confirmed);
        Assert.Equal(0, vm.ConfirmedCount);
        Assert.Empty(vm.BuildActions());
    }

    [Fact]
    public void 逐条勾选_汇总与执行清单正确()
    {
        var vm = new CleanupQueueViewModel(new[]
        {
            It(@"D:\缓存\a.tmp", size: 1024),
            It(@"D:\缓存\b.tmp", size: 2048),
            It(@"D:\缓存\c.tmp", size: 4096),
        });

        Assert.True(vm.Toggle(@"D:\缓存\b.tmp"));
        Assert.True(vm.Toggle(@"D:\缓存\c.tmp"));
        Assert.Equal(2, vm.ConfirmedCount);
        Assert.Equal(2048 + 4096, vm.ConfirmedBytes);

        var actions = vm.BuildActions();
        Assert.Equal(2, actions.Count);                           // 未确认的 a.tmp 不进清单
        Assert.All(actions, a => Assert.True(a.Confirmed));
        Assert.Equal(@"D:\缓存\c.tmp", actions[1].FullPath);
    }

    [Fact]
    public void Toggle不存在的路径或拒绝项_返回false()
    {
        var vm = new CleanupQueueViewModel(new[] { It(@"C:\Windows\x.log") });
        Assert.False(vm.Toggle(@"D:\不存在.log"));
        Assert.False(vm.Toggle(@"C:\Windows\x.log"));             // 拒绝项不可经 Toggle 勾上
        Assert.Equal(0, vm.ConfirmedCount);
    }

    [Fact]
    public void 全不选_清空勾选与汇总()
    {
        var vm = new CleanupQueueViewModel(new[] { It(@"D:\a.tmp"), It(@"D:\b.tmp") });
        vm.SelectAll();
        Assert.Equal(2, vm.ConfirmedCount);
        vm.ClearAll();
        Assert.Equal(0, vm.ConfirmedCount);
        Assert.Equal(0, vm.ConfirmedBytes);
        Assert.Empty(vm.BuildActions());
    }

    [Fact]
    public void 条目状态变化_上抛QueueChanged事件()
    {
        var vm = new CleanupQueueViewModel(new[] { It(@"D:\a.tmp") });
        int fired = 0;
        vm.QueueChanged += () => fired++;
        vm.Toggle(@"D:\a.tmp");
        vm.Toggle(@"D:\a.tmp");
        Assert.Equal(2, fired);
    }
}
