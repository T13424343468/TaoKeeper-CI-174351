using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>
/// B 管家线测试：DriverPageViewModel（清册/升级候选/备份计划/确认闸门/拦截标死）。
/// 验收口径：未确认零写动作（适配层不被触达）、哈希失配确认也不放行、备份未完成拒绝升级。
/// </summary>
public class LineB_DriverPageViewModelTests
{
    private static DriverUpdateEntry Entry(
        string id, string name, string? installed, string? available,
        bool critical = false, bool? hash = null,
        DriverUpdateKind kind = DriverUpdateKind.VendorPackagePnpUtil) =>
        new(id, name, installed, available, kind, critical, hash);

    /// <summary>假适配器：记录 Backup/Apply 是否被触达（零写动作核验的观测点）。</summary>
    private sealed class RecordingAdapter(params DriverUpdateEntry[] entries) : IDriverUpdateAdapter
    {
        public int BackupCalls;
        public int ApplyCalls;
        public bool BackupOk = true;

        public Task<IReadOnlyList<DriverUpdateEntry>> ListDriverUpdatesAsync(CancellationToken cancellation = default) =>
            Task.FromResult((IReadOnlyList<DriverUpdateEntry>)entries);

        public Task<bool> BackupDriverAsync(string deviceId, string backupDirectory, CancellationToken cancellation = default)
        {
            BackupCalls++;
            return Task.FromResult(BackupOk);
        }

        public Task<DriverActionResult> ApplyDriverUpdateAsync(DriverUpdateEntry driver, CancellationToken cancellation = default)
        {
            ApplyCalls++;
            return Task.FromResult(new DriverActionResult(driver.DeviceId, Success: true, BackupCompleted: true, "已升级（模拟）"));
        }
    }

    [Fact]
    public async Task 扫描清册_全部行含已是最新_状态与来源如实()
    {
        var adapter = new RecordingAdapter(
            Entry("gpu", "NVIDIA 显卡", "565.xx", "566.xx", critical: true),
            Entry("igpu", "Intel 核显驱动", "32.0.1", "32.0.1", kind: DriverUpdateKind.WuaDriver),
            Entry("audio", "Realtek 声卡", "6.0.95", "6.0.95"),
            Entry("hash", "失配设备", "1.0", "2.0", hash: false));

        var vm = await new DriverPageViewModel().LoadAsync(adapter);

        Assert.Equal(4, vm.ScannedCount);
        Assert.Equal(4, vm.Rows.Count); // 已是最新行照常展示（原型「—」行）
        var gpu = vm.Rows.First(r => r.DeviceId == "gpu");
        Assert.Equal(DriverRowState.UpgradeNeedConfirm, gpu.State); // 高危：必须逐项确认
        Assert.Equal("厂商官网", gpu.SourceText);
        Assert.Contains("pnputil /export-driver", gpu.BackupCommand); // 备份计划随行给出
        Assert.Equal("已是最新", vm.Rows.First(r => r.DeviceId == "igpu").AvailableVersionText);
        Assert.Equal("Windows 更新", vm.Rows.First(r => r.DeviceId == "igpu").SourceText);
        Assert.Equal(DriverRowState.BlockedByPolicy, vm.Rows.First(r => r.DeviceId == "hash").State);
        Assert.True(vm.HasUpgrades);
        Assert.Contains("哈希失配拦截 1 个", vm.SummaryText);
    }

    [Fact]
    public async Task 未确认触发升级_适配层零触达_返回拒绝()
    {
        var adapter = new RecordingAdapter(Entry("gpu", "NVIDIA 显卡", "565.xx", "566.xx", critical: true));
        var vm = await new DriverPageViewModel().LoadAsync(adapter);

        var result = await vm.ExecuteUpgradeAsync(adapter, "gpu"); // 未确认直接点升级
        Assert.False(result.Success);
        Assert.Equal(0, adapter.BackupCalls); // 零写动作：备份与升级都未被触达
        Assert.Equal(0, adapter.ApplyCalls);
        Assert.Contains("尚未经你确认", result.Detail);
    }

    [Fact]
    public async Task 确认后高危升级_备份先行且用户确认标志传入()
    {
        var adapter = new RecordingAdapter(Entry("gpu", "NVIDIA 显卡", "565.xx", "566.xx", critical: true));
        var vm = await new DriverPageViewModel().LoadAsync(adapter);

        Assert.True(vm.ConfirmUpgrade("gpu"));
        Assert.True(vm.IsConfirmed("gpu"));
        var result = await vm.ExecuteUpgradeAsync(adapter, "gpu");

        Assert.True(result.Success);          // 适配层模拟成功
        Assert.True(result.BackupCompleted);  // 备份先行（Core 闸门）
        Assert.Equal(1, adapter.BackupCalls);
        Assert.Equal(1, adapter.ApplyCalls);
    }

    [Fact]
    public async Task 哈希失配_确认也不放行_标死()
    {
        var adapter = new RecordingAdapter(Entry("bad", "失配设备", "1.0", "2.0", hash: false));
        var vm = await new DriverPageViewModel().LoadAsync(adapter);

        Assert.False(vm.ConfirmUpgrade("bad"));      // 确认不成立
        var result = await vm.ExecuteUpgradeAsync(adapter, "bad", policy: new DriverUpdatePolicy(RejectOnHashMismatch: true));
        Assert.False(result.Success);
        Assert.Equal(0, adapter.ApplyCalls);
        Assert.Equal(DriverActionResult.HashMismatchDetail, result.Detail);
        Assert.Equal(DriverRowState.BlockedByPolicy, vm.Rows.Single().State);
    }

    [Fact]
    public async Task 备份失败_升级拒绝且适配层未被触达()
    {
        var adapter = new RecordingAdapter(Entry("net", "无线网卡", "23.20", "23.40", critical: true)) { BackupOk = false };
        var vm = await new DriverPageViewModel().LoadAsync(adapter);
        vm.ConfirmUpgrade("net");

        var result = await vm.ExecuteUpgradeAsync(adapter, "net");
        Assert.False(result.Success);
        Assert.False(result.BackupCompleted);
        Assert.Equal(DriverActionResult.NoBackupDetail, result.Detail);
        Assert.Equal(0, adapter.ApplyCalls); // 备份失败即止，不进升级
    }

    [Fact]
    public async Task 新扫描后旧确认失效_撤销与未知设备拒绝()
    {
        var adapter = new RecordingAdapter(Entry("a", "设备A", "1.0", "2.0"));
        var vm = await new DriverPageViewModel().LoadAsync(adapter);
        vm.ConfirmUpgrade("a");
        Assert.True(vm.IsConfirmed("a"));

        await vm.LoadAsync(adapter); // 重新扫描
        Assert.False(vm.IsConfirmed("a"));           // 旧确认失效

        var unknown = await vm.ExecuteUpgradeAsync(adapter, "not-exist");
        Assert.False(unknown.Success);
        Assert.Equal(0, adapter.ApplyCalls);

        vm.ConfirmUpgrade("a");
        Assert.True(vm.RevokeConfirmation("a"));
        Assert.False(vm.IsConfirmed("a"));
    }

    [Fact]
    public async Task 无升级项_概览与状态如实()
    {
        var adapter = new RecordingAdapter(Entry("a", "设备A", "1.0", "1.0"));
        var vm = await new DriverPageViewModel().LoadAsync(adapter);

        Assert.False(vm.HasUpgrades);
        Assert.Equal(0, vm.BlockedCount);
        Assert.Contains("驱动全部为最新版本", vm.SummaryText);
        Assert.Equal(DriverRowState.UpToDate, vm.Rows.Single().State);
    }
}
