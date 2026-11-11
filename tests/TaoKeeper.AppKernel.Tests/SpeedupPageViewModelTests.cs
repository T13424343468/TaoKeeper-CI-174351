using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>一键加速页视图模型测试：计划只列不执行、勾选即确认、未勾绝不执行、回执转述。</summary>
public class SpeedupPageViewModelTests
{
    /// <summary>记录调用并返回成功的测试台架：核对「勾了才执行、执行了什么」。</summary>
    private sealed class RecordingAdapter(IReadOnlyList<StartupItemEntry> items) : IStartupControlAdapter
    {
        public List<(string ItemId, bool Enable)> Calls { get; } = new();

        public Task<IReadOnlyList<StartupItemEntry>> ListStartupItemsAsync(CancellationToken cancellation = default)
            => Task.FromResult(items);

        public Task<StartupToggleResult> ToggleStartupAsync(string itemId, bool enable, CancellationToken cancellation = default)
        {
            Calls.Add((itemId, enable));
            return Task.FromResult(new StartupToggleResult(itemId, enable, true, "已禁用，下次开机生效"));
        }
    }

    private static StartupItemEntry High(string id = "h1") =>
        new(id, "某推广助手", StartupItemSource.RegistryRun, "未知", true,
            StartupRiskLevel.High, SpeedupPolicy.HighRiskReason);

    private static StartupItemEntry Medium(string id = "m1") =>
        new(id, "未确认发布者", StartupItemSource.RegistryRun, "未知", true,
            StartupRiskLevel.Medium, SpeedupPolicy.MediumRiskReason);

    private static MemorySnapshot HighMem() =>
        new(DateTimeOffset.UtcNow, 16L * 1024 * 1024 * 1024,
            1L * 1024 * 1024 * 1024, null, null);

    [Fact]
    public async Task 未刷新为未测态_刷新只列建议不执行()
    {
        var adapter = new RecordingAdapter(new[] { High(), Medium() });
        var vm = new SpeedupPageViewModel(new SpeedupCoordinator(adapter));
        Assert.Equal("尚未体检加速", vm.SummaryText);
        Assert.Null(vm.Plan);

        await vm.RefreshPlanAsync(HighMem());
        Assert.Equal("共 3 项可优化，逐条确认后执行", vm.SummaryText);
        Assert.Single(vm.DisableSuggestions);
        Assert.Single(vm.ReviewSuggestions);
        Assert.True(vm.MemoryTrimSuggested);
        Assert.Empty(adapter.Calls);            // 计划阶段零写动作
    }

    [Fact]
    public async Task 空清单摘要为已很清爽()
    {
        var vm = new SpeedupPageViewModel(new SpeedupCoordinator(new RecordingAdapter(Array.Empty<StartupItemEntry>())));
        await vm.RefreshPlanAsync();
        Assert.Equal("当前已很清爽，无需加速", vm.SummaryText);
        Assert.False(vm.MemoryTrimSuggested);
    }

    [Fact]
    public async Task 只执行用户勾选项_未勾绝不进执行清单()
    {
        var adapter = new RecordingAdapter(new[] { High("h1"), High("h2") });
        var vm = new SpeedupPageViewModel(new SpeedupCoordinator(adapter));
        await vm.RefreshPlanAsync();

        var results = await vm.ApplyConfirmedAsync(new[] { "h1" });   // 只勾 h1
        Assert.Single(results);
        Assert.Equal("h1", results[0].ItemId);
        Assert.True(results[0].Success);
        Assert.Single(adapter.Calls);
        Assert.Equal(("h1", false), adapter.Calls[0]);
        Assert.Contains("成功 1", vm.ResultsSummaryText);
    }

    [Fact]
    public async Task 空勾选直接返回_不触达适配层()
    {
        var adapter = new RecordingAdapter(new[] { High() });
        var vm = new SpeedupPageViewModel(new SpeedupCoordinator(adapter));
        await vm.RefreshPlanAsync();

        var results = await vm.ApplyConfirmedAsync(Array.Empty<string>());
        Assert.Empty(results);
        Assert.Empty(adapter.Calls);
        Assert.Equal("本次未执行任何加速动作", vm.ResultsSummaryText);
    }

    [Fact]
    public async Task 建议与回执文案转述()
    {
        var adapter = new RecordingAdapter(new[] { High("h1") });
        var vm = new SpeedupPageViewModel(new SpeedupCoordinator(adapter));
        await vm.RefreshPlanAsync();

        var text = SpeedupPageViewModel.Describe(vm.DisableSuggestions[0]);
        Assert.Contains("某推广助手", text);
        Assert.Contains("流氓推广", text);

        await vm.ApplyConfirmedAsync(new[] { "h1" });
        Assert.Contains("已禁用，下次开机生效", vm.LastResults[0].DisplayText);
    }
}
