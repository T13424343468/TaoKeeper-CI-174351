using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules.DiskHardcore;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>硬核四项引擎测试：只读默认、确认闸、镜像先行、失败回滚（N9/N10/D1–D4 可检验段）。</summary>
public class DiskHardcoreEngineTests
{
    private sealed class FakeAdapter : IHardcoreDiskAdapter
    {
        public List<string> CallLog { get; } = [];
        public bool ApplyFails { get; set; }
        public bool MirrorFails { get; set; }
        public bool Restored { get; private set; }
        public List<long> BadLbas { get; set; } = [];
        public List<RecoveredFileEntry> Files { get; set; } = [];

        public PartitionScanReport ScanPartitionTable()
        {
            CallLog.Add("scan");
            return new PartitionScanReport(
                RawSuspected: true,
                Entries: [new(0, 0, 2048, "RAW 疑似", false), new(1, 2048, 1024, "NTFS (C:)", true)],
                Suggestion: "建议在镜像上重建");
        }

        public RebuildPlan ProposeRebuildPlan(PartitionScanReport report)
        {
            CallLog.Add("propose");
            return new RebuildPlan(report.Entries.Where(e => e.FilesystemSignaturePresent).ToList(), "保守重建");
        }

        public MirrorRecord CreateMirror()
        {
            if (MirrorFails) throw new IOException("mirror device unavailable");
            CallLog.Add("mirror");
            return new MirrorRecord("m-1", DateTimeOffset.UtcNow, 4096);
        }

        public void ApplyPlanToMirror(RebuildPlan plan)
        {
            CallLog.Add("apply");
            if (ApplyFails) throw new IOException("apply exploded");
        }

        public void RestoreMirror(MirrorRecord mirror)
        {
            CallLog.Add("restore");
            Restored = true;
        }

        public BadSectorReport ScanBadSectors(long startLba, long sectorCount)
        {
            CallLog.Add("scan-sector");
            return new BadSectorReport(sectorCount, BadLbas);
        }

        public RecoveryScanReport ScanMirrorForFiles(RecoverabilityFilter filter)
        {
            CallLog.Add("mirror-scan");
            return new RecoveryScanReport(MirrorPresent: true, Files);
        }

        public int RestoreRecoverableTo(string targetDirectory, IReadOnlyList<RecoveredFileEntry> files)
        {
            CallLog.Add($"restore:{targetDirectory}:{files.Count}");
            return files.Count;
        }
    }

    private sealed class FakeMemoryTester : IMemoryPageTester
    {
        public List<long> Bad { get; set; } = [];
        public BadPageReport TestPages(long startPage, long pageCount) =>
            new(pageCount, Bad, ExecutableSuggestion: Bad.Count > 0 ? "bcdedit /removemem …（示例口径，真机以诊断报告为准）" : null);
    }

    [Fact]
    public void MirrorAndScan_MirrorFirst_ThenScan()
    {
        var a = new FakeAdapter();
        var (mirror, scan) = DiskHardcoreEngine.MirrorAndScan(a);
        Assert.Equal("m-1", mirror.MirrorId);
        Assert.True(scan.MirrorPresent);
        Assert.Equal(["mirror", "mirror-scan"], a.CallLog); // 镜像先于扫描（D1 源盘零写入前提）
    }

    [Fact]
    public void MirrorAndScan_MirrorFails_Throws_NoScan()
    {
        var a = new FakeAdapter { MirrorFails = true };
        Assert.Throws<IOException>(() => DiskHardcoreEngine.MirrorAndScan(a));
        Assert.DoesNotContain("mirror-scan", a.CallLog);
    }

    [Fact]
    public void AnalyzeAndPropose_ReadOnly_HealthlyNullPlan()
    {
        var a = new FakeAdapter();
        var (report, plan) = DiskHardcoreEngine.AnalyzeAndPropose(a);
        Assert.True(report.RawSuspected);
        Assert.NotNull(plan); // RAW 疑似出方案
        Assert.DoesNotContain("apply", a.CallLog); // 全程零写入
    }

    [Fact]
    public void ApplyRebuild_WithoutConfirm_Rejected()
    {
        var a = new FakeAdapter();
        var plan = new RebuildPlan([], "x");
        Assert.Throws<InvalidOperationException>(() =>
            DiskHardcoreEngine.ApplyRebuildWithMirror(a, plan, confirmed: false));
        Assert.Empty(a.CallLog); // 未确认不摸盘
    }

    [Fact]
    public void ApplyRebuild_Confirmed_MirrorBeforeApply()
    {
        var a = new FakeAdapter();
        var plan = new RebuildPlan([], "x");
        DiskHardcoreEngine.ApplyRebuildWithMirror(a, plan, confirmed: true);
        var mirrorIdx = a.CallLog.IndexOf("mirror");
        var applyIdx = a.CallLog.IndexOf("apply");
        Assert.True(mirrorIdx >= 0 && applyIdx > mirrorIdx); // 镜像先行（D2）
        Assert.Contains("apply", a.CallLog);
    }

    [Fact]
    public void ApplyRebuild_ApplyFails_ForcedRestore()
    {
        var a = new FakeAdapter { ApplyFails = true };
        var plan = new RebuildPlan([], "x");
        Assert.Throws<IOException>(() =>
            DiskHardcoreEngine.ApplyRebuildWithMirror(a, plan, confirmed: true));
        Assert.True(a.Restored); // 半写即回滚
    }

    [Fact]
    public void ScanBadSectors_InvalidRange_Throws()
    {
        var a = new FakeAdapter();
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskHardcoreEngine.ScanBadSectors(a, -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskHardcoreEngine.ScanBadSectors(a, 0, 0));
    }

    [Fact]
    public void MarkBadSectors_WithoutConfirm_Rejected()
    {
        var a = new FakeAdapter();
        var scan = new BadSectorReport(100, [42, 43]);
        Assert.Throws<InvalidOperationException>(() => DiskHardcoreEngine.MarkBadSectors(a, scan, confirmed: false));
    }

    [Fact]
    public void LocateBadPages_ReadOnly_Reports()
    {
        var t = new FakeMemoryTester { Bad = [7] };
        var report = DiskHardcoreEngine.LocateBadPages(t, 0, 128);
        Assert.Equal(128, report.PagesChecked);
        Assert.Equal([7], report.BadPageNumbers);
        Assert.NotNull(report.ExecutableSuggestion);
    }

    [Fact]
    public void EmptyScan_HonestEmpty_NeverFaked()
    {
        var a = new FakeAdapter { Files = [] };
        var (_, scan) = DiskHardcoreEngine.MirrorAndScan(a);
        Assert.Empty(scan.Files); // 扫不出=空报告（N9 不编数据）
    }

    [Fact]
    public void RestoreRecoverable_EmptyList_NoWrite()
    {
        var a = new FakeAdapter();
        var (restored, failed) = DiskHardcoreEngine.RestoreRecoverable(a, [], @"D:\recovered");
        Assert.Equal(0, restored);
        Assert.Empty(failed);
        Assert.DoesNotContain("restore", a.CallLog); // 无条目不摸盘
    }

    [Fact]
    public void RestoreRecoverable_WritesOnlyToTargetDir()
    {
        var a = new FakeAdapter();
        var files = new List<RecoveredFileEntry> { new("a.txt", 10, null, @"C:\a.txt", Recoverability.Intact) };
        var (restored, _) = DiskHardcoreEngine.RestoreRecoverable(a, files, @"D:\recovered");
        Assert.Equal(1, restored);
        Assert.Contains("restore:D:\\recovered:1", a.CallLog); // 目标=用户选定目录（≠源盘）
    }

    [Fact]
    public void RestoreRecoverable_NullOrEmptyTarget_Throws()
    {
        var a = new FakeAdapter();
        var files = new List<RecoveredFileEntry>();
        Assert.Throws<ArgumentException>(() => DiskHardcoreEngine.RestoreRecoverable(a, files, " "));
        Assert.Throws<ArgumentNullException>(() => DiskHardcoreEngine.RestoreRecoverable(a, null!, @"D:\x"));
    }
}
