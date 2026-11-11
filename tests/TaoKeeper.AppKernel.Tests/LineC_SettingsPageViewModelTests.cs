using System;
using System.IO;
using TaoKeeper.AppKernel.Settings;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.AppKernel.ViewModels;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>设置页视图模型测试：默认态、读写往返、非法输入防呆、文案映射与确认闸门（C 工具设置线）。</summary>
public class LineC_SettingsPageViewModelTests
{
    /// <summary>独立临时设置文件（每用例自带路径，测试后清理，不污染工程树）。</summary>
    private sealed class TempSettingsStore : IDisposable
    {
        public string Path { get; }
        public AppSettingsStore Store { get; }

        public TempSettingsStore()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tk-linec-{Guid.NewGuid():N}", "settings.json");
            Store = new AppSettingsStore(Path);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(System.IO.Path.GetDirectoryName(Path))) Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true); }
            catch (IOException) { }
        }
    }

    // ── 1. 默认态：字段默认值与原型 v19 设置页开关初始态逐项一致，无假数据、无待确认改动 ──

    [Fact]
    public void 默认态与原型开关初始态逐项一致()
    {
        var vm = new SettingsPageViewModel();

        // 通用：主题默认水墨；开机自启/自动体检默认开
        Assert.Equal(ThemeName.Shuimo, vm.Current.Theme);
        Assert.True(vm.Current.LaunchOnStartup);
        Assert.True(vm.Current.AutoCheckupOnStartup);

        // 软件管家：静默升级默认关，快捷方式/安装账默认开，默认安装位置与模型一致
        Assert.False(vm.Current.SilentUpgrade);
        Assert.True(vm.Current.CreateShortcutAfterInstall);
        Assert.True(vm.Current.RecordInstallLedger);
        Assert.Equal(@"D:\TaoKeeper\Soft", vm.Current.DefaultInstallDir);

        // 清理与存储：存储感知默认关，快扫默认开
        Assert.False(vm.Current.StorageSenseEnabled);
        Assert.True(vm.Current.QuickScanByDefault);

        // 挂件：默认显示 内存+垃圾待清，标准档
        Assert.True(vm.Current.ShowDesktopWidget);
        Assert.Equal(2, vm.Current.WidgetContents.Count);
        Assert.Contains(WidgetContent.Memory, vm.Current.WidgetContents);
        Assert.Contains(WidgetContent.JunkPending, vm.Current.WidgetContents);
        Assert.Equal(WidgetSize.Standard, vm.Current.WidgetSize);

        // 阈值默认：内存 85% / 磁盘 10 GB
        Assert.Equal(85, vm.Current.MemoryAlertPercent);
        Assert.Equal(10.0, vm.Current.DiskFreeAlertGb);

        // 默认零写动作：无待确认改动，未接存储
        Assert.False(vm.HasPendingChanges);
        Assert.Equal(0, vm.PendingChangeCount);
        Assert.False(vm.HasStore);
        Assert.Equal("暂无待确认改动。", vm.PendingSummaryText);
    }

    // ── 2. 读写往返：确认闸门落盘 → 新 VM 读回；既有 Store 存过的设置也能读出 ──

    [Fact]
    public void 读写往返经既有存储契约()
    {
        using var temp = new TempSettingsStore();

        // 待确认 → 确认（写设置文件）→ 新 VM 读回，值逐项一致
        var vm1 = new SettingsPageViewModel(temp.Store);
        vm1.SetPendingTheme(ThemeName.Tianqing);
        vm1.SetPendingSilentUpgrade(true);
        vm1.SetPendingWidgetSize(WidgetSize.Compact);
        vm1.TogglePendingWidgetContent(WidgetContent.DiskFree);
        Assert.True(vm1.TrySetPendingMemoryAlertPercent(70, out _));
        Assert.True(vm1.TrySetPendingDiskFreeAlertGb(15.5, out _));
        Assert.True(vm1.TrySetPendingDefaultInstallDir(@"E:\Soft\Tao", out _));

        var summaries = vm1.ConfirmPendingChanges();
        Assert.Equal(7, summaries.Count);
        Assert.True(temp.Store.FilePath is not null && File.Exists(temp.Store.FilePath));

        var vm2 = new SettingsPageViewModel(temp.Store);
        Assert.Equal(ThemeName.Tianqing, vm2.Current.Theme);
        Assert.True(vm2.Current.SilentUpgrade);
        Assert.Equal(WidgetSize.Compact, vm2.Current.WidgetSize);
        Assert.Contains(WidgetContent.DiskFree, vm2.Current.WidgetContents);
        Assert.Equal(70, vm2.Current.MemoryAlertPercent);
        Assert.Equal(15.5, vm2.Current.DiskFreeAlertGb);
        Assert.Equal(@"E:\Soft\Tao", vm2.Current.DefaultInstallDir);
        Assert.False(vm2.HasPendingChanges); // 读回即生效，无待确认改动

        // 既有 Store 存过的设置（不经 VM 确认）也能读出
        var external = new AppSettings { Theme = ThemeName.Mobai, MemoryAlertPercent = 90 };
        temp.Store.Save(external);
        var vm3 = new SettingsPageViewModel(temp.Store);
        Assert.Equal(ThemeName.Mobai, vm3.Current.Theme);
        Assert.Equal(90, vm3.Current.MemoryAlertPercent);
    }

    // ── 3. 非法输入防呆：越界/空值一律拒绝，待确认值与文件零触碰 ──

    [Fact]
    public void 非法输入防呆不记入待确认值()
    {
        using var temp = new TempSettingsStore();
        var vm = new SettingsPageViewModel(temp.Store);

        // 内存告警线：只接受 0–100
        Assert.False(vm.TrySetPendingMemoryAlertPercent(101, out var e1));
        Assert.Contains("0–100", e1);
        Assert.False(vm.TrySetPendingMemoryAlertPercent(-1, out _));
        Assert.True(vm.TrySetPendingMemoryAlertPercent(0, out _));    // 0=关，合法
        Assert.True(vm.TrySetPendingMemoryAlertPercent(100, out _));  // 上界合法

        // 磁盘余量提醒：只接受 0–1024，非数值拒绝
        Assert.False(vm.TrySetPendingDiskFreeAlertGb(-0.5, out _));
        Assert.False(vm.TrySetPendingDiskFreeAlertGb(double.NaN, out _));
        Assert.True(vm.TrySetPendingDiskFreeAlertGb(0, out _));

        // 默认安装位置：空值拒绝
        Assert.False(vm.TrySetPendingDefaultInstallDir("", out var e3));
        Assert.Contains("不能为空", e3);
        Assert.False(vm.TrySetPendingDefaultInstallDir("   ", out _));

        // 防呆后待确认值仍是合法改动（100/0/新位置），文件零触碰
        Assert.True(vm.TrySetPendingDefaultInstallDir(@"E:\Soft\Tao", out _));
        Assert.Equal(3, vm.PendingChangeCount);
        Assert.False(File.Exists(temp.Store.FilePath));
        Assert.Equal(85, vm.Current.MemoryAlertPercent); // 确认闸门前 Current 不动

        // 防呆项经 Confirm 后落盘的是合法值
        vm.ConfirmPendingChanges();
        Assert.Equal(100, vm.Current.MemoryAlertPercent);
        Assert.Equal(0, vm.Current.DiskFreeAlertGb);
    }

    // ── 4. 文案映射：主题/挂件/大小目录、红线锁定 pill、阈值行如实 ──

    [Fact]
    public void 文案映射与既有模型目录同源()
    {
        var vm = new SettingsPageViewModel();

        // 主题三选：水墨（默认）/ 天青 / 墨白
        Assert.Equal(3, SettingsPageViewModel.ThemeCatalog.Count);
        Assert.Equal(("水墨", "天青", "墨白"),
            (SettingsPageViewModel.ThemeCatalog[0].DisplayText, SettingsPageViewModel.ThemeCatalog[1].DisplayText, SettingsPageViewModel.ThemeCatalog[2].DisplayText));

        // 挂件内容 / 大小
        Assert.Equal(new[] { "内存", "垃圾待清", "磁盘余量" },
            new[] { SettingsPageViewModel.WidgetContentCatalog[0].DisplayText, SettingsPageViewModel.WidgetContentCatalog[1].DisplayText, SettingsPageViewModel.WidgetContentCatalog[2].DisplayText });
        Assert.Equal(new[] { "标准", "紧凑" },
            new[] { SettingsPageViewModel.WidgetSizeCatalog[0].DisplayText, SettingsPageViewModel.WidgetSizeCatalog[1].DisplayText });

        // 红线锁定 pill：三条，与模型常量逐字一致
        Assert.Equal(3, vm.LockedPills.Count);
        Assert.Equal(AppSettingsGuard.OfficialSourceLockedMessage, vm.LockedPills[0]);
        Assert.Equal(AppSettingsGuard.HashCheckLockedMessage, vm.LockedPills[1]);
        Assert.Equal(AppSettingsGuard.RecycleBinLockedMessage, vm.LockedPills[2]);

        // 阈值行文案随待确认值如实切换（默认 / 调整 / 关闭）
        Assert.Contains("85%", vm.MemoryAlertLine);
        Assert.Contains("10", vm.DiskFreeAlertLine);
        Assert.True(vm.TrySetPendingMemoryAlertPercent(70, out _));
        Assert.Contains("70%", vm.MemoryAlertLine);
        Assert.True(vm.TrySetPendingMemoryAlertPercent(0, out _));
        Assert.Equal("已关闭（0=不提醒）", vm.MemoryAlertLine);
        Assert.True(vm.TrySetPendingDiskFreeAlertGb(0, out _));
        Assert.Equal("已关闭（0=不提醒）", vm.DiskFreeAlertLine);
    }

    // ── 5. 确认闸门：改动先进待确认副本，保存才生效；无存储渠道时只在本会话生效并如实标明 ──

    [Fact]
    public void 确认闸门与无存储渠道如实说明()
    {
        using var temp = new TempSettingsStore();

        // 未接存储：确认只改内存，不落盘；文案如实说明
        var vmNoStore = new SettingsPageViewModel();
        vmNoStore.SetPendingTheme(ThemeName.Mobai);
        Assert.True(vmNoStore.HasPendingChanges);
        Assert.Contains("仅本会话", vmNoStore.PendingSummaryText);
        Assert.Equal("保存改动（仅本会话生效）", vmNoStore.PendingSaveText);
        var applied = vmNoStore.ConfirmPendingChanges();
        Assert.Single(applied);
        Assert.Equal(ThemeName.Mobai, vmNoStore.Current.Theme);
        Assert.False(File.Exists(temp.Store.FilePath));

        // 已接存储：确认前 Current 不动、文件不存在；放弃则待确认副本回滚、文件零触碰
        var vm = new SettingsPageViewModel(temp.Store);
        vm.SetPendingStorageSenseEnabled(true);
        Assert.False(vm.Current.StorageSenseEnabled);
        Assert.False(File.Exists(temp.Store.FilePath));
        vm.DiscardPendingChanges();
        Assert.False(vm.HasPendingChanges);
        Assert.Equal("暂无待确认改动。", vm.PendingSummaryText);
        vm.ConfirmPendingChanges(); // 无改动：不触碰存储
        Assert.False(File.Exists(temp.Store.FilePath));
    }
}
