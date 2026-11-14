using System;
using System.Collections.Generic;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules.DiskHardcore;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>硬核四项 VM 测试：只读默认、确认后写、诚实空态（V-A10/N9）。</summary>
public class DiskHardcorePageViewModelTests
{
    private sealed class FakeAdapter : IHardcoreDiskAdapter
    {
        public List<string> CallLog { get; } = [];
        public bool RawSuspected { get; set; } = true;
        public List<RecoveredFileEntry> Files { get; set; } = [];
        public List<long> BadLbas { get; set; } = [];

        public PartitionScanReport ScanPartitionTable() => new(
            RawSuspected, [new(0, 0, 2048, "RAW 疑似", !RawSuspected)],
            RawSuspected ? "建议在镜像上重建" : null);

        public RebuildPlan ProposeRebuildPlan(PartitionScanReport report) =>
            new(report.Entries.Where(e => e.FilesystemSignaturePresent).ToList(), "保守重建");

        public MirrorRecord CreateMirror() { CallLog.Add("mirror"); return new("m-1", DateTimeOffset.UtcNow, 4096); }
        public void ApplyPlanToMirror(RebuildPlan plan) => CallLog.Add("apply");
        public void RestoreMirror(MirrorRecord mirror) => CallLog.Add("restore");

        public BadSectorReport ScanBadSectors(long startLba, long sectorCount) =>
            new(sectorCount, BadLbas);

        public RecoveryScanReport ScanMirrorForFiles(RecoverabilityFilter filter) =>
            new(MirrorPresent: true, Files);

        public int RestoreRecoverableTo(string targetDirectory, IReadOnlyList<RecoveredFileEntry> files)
        {
            RestoredCount = files.Count;
            return files.Count;
        }

        public int? RestoredCount { get; private set; }
    }

    [Fact]
    public void MirrorAndScan_Populates_Rows_And_Files()
    {
        var vm = new DiskHardcorePageViewModel();
        var adapter = new FakeAdapter
        {
            Files =
            [
                new("合同.docx", 2048, new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero), @"D:\docs", Recoverability.Intact),
            ],
        };
        vm.MirrorAndScan(adapter);

        Assert.True(vm.IsScanned);
        Assert.True(vm.MirrorPresent);
        Assert.Equal("m-1", vm.MirrorId);
        Assert.True(vm.RawSuspected);
        Assert.True(vm.HasRebuildPlan);
        Assert.Contains(vm.Rows, r => r.ToolLabel == "恢复前镜像");
        Assert.Contains(vm.Rows, r => r.ToolLabel == "分区表判读");
        Assert.Contains(vm.Rows, r => r.ToolLabel == "坏道扫描");
        Assert.Single(vm.RecoverableFiles);
        Assert.Contains("完好可救", vm.RecoverableFiles[0]);
    }

    [Fact]
    public void MirrorAndScan_EmptyResult_HonestEmptyText()
    {
        var vm = new DiskHardcorePageViewModel();
        vm.MirrorAndScan(new FakeAdapter());
        Assert.Contains(vm.Rows, r => r.ToolLabel == "文件扫描" && r.Summary == vm.NoResultText);
        Assert.Empty(vm.RecoverableFiles);
    }

    [Fact]
    public void ApplyRebuild_OnlyAfterScan()
    {
        var vm = new DiskHardcorePageViewModel();
        Assert.False(vm.ApplyRebuild()); // 未扫描=无方案，不执行

        var vm2 = new DiskHardcorePageViewModel();
        var adapter = new FakeAdapter { RawSuspected = false };
        vm2.MirrorAndScan(adapter);
        Assert.False(vm2.HasRebuildPlan); // 分区正常不出方案
        Assert.False(vm2.ApplyRebuild());
    }

    [Fact]
    public void ApplyRebuild_ConfirmedPath_MirrorThenApply()
    {
        var vm = new DiskHardcorePageViewModel();
        var adapter = new FakeAdapter();
        vm.MirrorAndScan(adapter);
        var callsBefore = adapter.CallLog.Count;
        Assert.True(vm.ApplyRebuild());
        Assert.Equal(["mirror", "apply"], adapter.CallLog.Skip(callsBefore));
        Assert.False(vm.HasRebuildPlan); // 已执行
        Assert.Contains("源盘未动", vm.RebuildSuggestion);
    }

    [Fact]
    public void MarkBadSectors_RequiresPriorScan()
    {
        var vm = new DiskHardcorePageViewModel();
        Assert.False(vm.MarkBadSectors());
    }

    [Fact]
    public void RestoreTo_RequiresPriorScan()
    {
        var vm = new DiskHardcorePageViewModel();
        var (restored, message) = vm.RestoreTo(@"D:\out");
        Assert.Equal(0, restored);
        Assert.Equal(vm.EmptyText, message);
    }

    [Fact]
    public void RestoreTo_NoIntact_FailsHonestly()
    {
        var vm = new DiskHardcorePageViewModel();
        vm.MirrorAndScan(new FakeAdapter()); // 无文件
        var (restored, message) = vm.RestoreTo(@"D:\out");
        Assert.Equal(0, restored);
        Assert.Equal(vm.NoResultText, message);
    }

    [Fact]
    public void RestoreTo_IntactOnly_ExportsToChosenDir()
    {
        var vm = new DiskHardcorePageViewModel();
        var adapter = new FakeAdapter
        {
            Files =
            [
                new("a.txt", 10, null, @"C:\a.txt", Recoverability.Intact),
                new("b.bin", 20, null, @"C:\b.bin", Recoverability.Overwritten),
            ],
        };
        vm.MirrorAndScan(adapter);
        var (restored, message) = vm.RestoreTo(@"D:\out");
        Assert.Equal(1, restored); // 只导出完好可救
        Assert.Equal(1, adapter.RestoredCount);
        Assert.Contains("1/1", message);
        Assert.Contains("源盘零改动", message);
    }
}
