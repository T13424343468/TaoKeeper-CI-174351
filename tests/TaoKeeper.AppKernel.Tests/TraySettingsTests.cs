using System;
using System.IO;
using TaoKeeper.AppKernel.Settings;
using TaoKeeper.AppKernel.ViewModels;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>托盘设置（R-P12-9）：模型默认值、待确认链路、改动摘要与确认落盘同源口径。</summary>
public class TraySettingsTests
{
    /// <summary>独立临时设置文件（沿用 LineC 打样的 TempSettingsStore 模式，每用例自带路径）。</summary>
    private sealed class TempSettingsStore : IDisposable
    {
        public string Path { get; }
        public AppSettingsStore Store { get; }

        public TempSettingsStore()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tk-tray-{Guid.NewGuid():N}", "settings.json");
            Store = new AppSettingsStore(Path);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(System.IO.Path.GetDirectoryName(Path))) Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void 默认值与原型托盘设置初始态一致()
    {
        var vm = new SettingsPageViewModel();
        Assert.True(vm.Current.LaunchOnStartup); // 开机自启单一开关默认开（原型通用卡初始态；托盘双口径 2026-11-14 已合并）
        Assert.True(vm.Current.TrayResident);    // 托盘常驻默认开（原型 TRAY.resident=true）
        Assert.False(vm.HasPendingChanges);
    }

    [Fact]
    public void 待确认改动进Pending_确认后写盘并可回读()
    {
        using var temp = new TempSettingsStore();
        var vm = new SettingsPageViewModel(temp.Store);

        vm.SetPendingLaunchOnStartup(false); // 单一开关统管：通用卡「开机自动启动」即托盘菜单「开机自启」
        vm.SetPendingTrayResident(false);

        Assert.True(vm.HasPendingChanges);
        Assert.True(vm.Current.LaunchOnStartup); // 确认闸门前 Current 不动
        Assert.True(vm.Current.TrayResident);

        var applied = vm.ConfirmPendingChanges();
        Assert.Equal(2, applied.Count);
        Assert.Contains("开机自动启动：开 → 关", applied);
        Assert.Contains("托盘常驻：开 → 关", applied);
        Assert.False(vm.Current.LaunchOnStartup);
        Assert.False(vm.Current.TrayResident);

        // 跨实例回读：落盘契约生效（AppSettingsStore 原子写 + Load 往返）
        var reloaded = new SettingsPageViewModel(temp.Store);
        Assert.False(reloaded.Current.LaunchOnStartup);
        Assert.False(reloaded.Current.TrayResident);
    }

    [Fact]
    public void 放弃改动_文件零触碰()
    {
        using var temp = new TempSettingsStore();
        var vm = new SettingsPageViewModel(temp.Store);

        vm.SetPendingLaunchOnStartup(false);
        vm.DiscardPendingChanges();

        Assert.False(vm.HasPendingChanges);
        Assert.True(vm.Current.LaunchOnStartup);
        Assert.False(File.Exists(temp.Path)); // 从未确认过，设置文件不该被创建
    }

    [Fact]
    public void 无改动确认_不触碰存储()
    {
        using var temp = new TempSettingsStore();
        var vm = new SettingsPageViewModel(temp.Store);

        var applied = vm.ConfirmPendingChanges();

        Assert.Empty(applied);
        Assert.False(File.Exists(temp.Path));
    }

    [Fact]
    public void 仅托盘常驻单项改动_摘要只含一行()
    {
        using var temp = new TempSettingsStore();
        var vm = new SettingsPageViewModel(temp.Store);

        vm.SetPendingTrayResident(false);

        Assert.Equal(1, vm.PendingChangeCount); // 只动了一项，其余字段零变化
        Assert.Equal("有 1 项改动待确认：点「保存改动」写入设置文件，或「放弃」。", vm.PendingSummaryText);
    }
}
