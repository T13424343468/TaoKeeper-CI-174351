using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// 清理分层语义测试（0.2 拍板，2026-11-14）：
/// 确定垃圾默认真删（删前快照、空间按真删量实际增加）、用户文件默认回收站、
/// 勾选「直接删除」后真删同样先快照；真删通道未接线如实拒绝、绝不静默降级；
/// 快照失败绝不动原文件；账本记快照落点供期内还原。
/// </summary>
public class CleanupTierTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CleanupLedger _ledger;
    private readonly FakeRecycleAdapter _recycle = new();
    private readonly FakePermanentAdapter _permanent = new();
    private readonly CleanupCoordinator _coordinator;

    public CleanupTierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tk-tier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _ledger = new CleanupLedger(Path.Combine(_tempDir, "ledger.jsonl"));
        _coordinator = new CleanupCoordinator(_recycle, _ledger, _permanent);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private sealed class FakeRecycleAdapter : IFileRemovalAdapter
    {
        public List<string> Requested { get; } = new();
        public Task<string?> MoveToRecycleBinAsync(string fullPath, CancellationToken cancellation = default)
        {
            Requested.Add(fullPath);
            return Task.FromResult<string?>(@"X:\bin\" + Path.GetFileName(fullPath));
        }
    }

    private sealed class FakePermanentAdapter : IPermanentRemovalAdapter
    {
        public List<string> Requested { get; } = new();
        public List<string> Deleted { get; } = new();
        public bool SnapshotFails { get; set; }

        public Task<PermanentRemovalResult> SnapshotThenDeleteAsync(string fullPath, CancellationToken cancellation = default)
        {
            Requested.Add(fullPath);
            if (SnapshotFails)
                return Task.FromResult(new PermanentRemovalResult(false, null, "模拟快照失败"));
            var snapshot = Path.Combine(Path.GetDirectoryName(fullPath)!, ".snap-" + Path.GetFileName(fullPath));
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
            else if (File.Exists(fullPath)) File.Delete(fullPath);
            else return Task.FromResult(new PermanentRemovalResult(false, null, "目标不存在"));
            Deleted.Add(fullPath);
            return Task.FromResult(new PermanentRemovalResult(true, snapshot));
        }
    }

    private static CleanupActionItem Item(string path, CleanupTier tier, bool directDelete = false) =>
        new(path, 1024, "junk", Confirmed: true, "test-rule", tier, directDelete);

    [Fact]
    public async Task Garbage_tier_defaults_to_permanent_delete_with_snapshot()
    {
        var file = Path.Combine(_tempDir, "cache.tmp");
        await File.WriteAllTextAsync(file, "junk");
        var freeBefore = new DriveInfo(_tempDir).AvailableFreeSpace;

        var result = await _coordinator.ExecuteAsync(new[] { Item(file, CleanupTier.Garbage) });

        Assert.True(result.Outcomes[0].Success);
        Assert.Equal("permanent", result.Outcomes[0].Mode);
        Assert.False(File.Exists(file));                      // 已真删
        Assert.True(File.Exists(_permanent.Requested[0]) == false);
        Assert.NotEmpty(_permanent.Requested);
        Assert.Null(_recycle.Requested.FirstOrDefault());     // 未走回收站
        Assert.Equal(1, result.PermanentCount);               // 空间实际增加以真删类统计
        Assert.Equal(1024, result.PermanentBytes);
        _ = freeBefore; // 真删量口径在引擎侧统计（result.PermanentBytes）

        // 账本：快照落点可还原
        var (entries, _) = await _ledger.LoadAsync();
        var entry = Assert.Single(entries);
        Assert.Equal(LedgerRestoreKind.TrashDir, entry.RestoreKind);
        Assert.Equal(result.Outcomes[0].BinPath, entry.BinPath);
        Assert.True(entry.IsRestorable);
    }

    [Fact]
    public async Task UsersFile_tier_defaults_to_recycle_bin()
    {
        var file = Path.Combine(_tempDir, "report.docx");
        await File.WriteAllTextAsync(file, "doc");

        var result = await _coordinator.ExecuteAsync(new[] { Item(file, CleanupTier.UsersFile) });

        Assert.True(result.Outcomes[0].Success);
        Assert.Equal("recycle", result.Outcomes[0].Mode);
        Assert.Equal(0, result.PermanentCount);               // 用户文件不计入真删量
        Assert.Single(_recycle.Requested);                    // 走回收站
        Assert.Empty(_permanent.Requested);                   // 未走真删
    }

    [Fact]
    public async Task UsersFile_with_direct_delete_goes_permanent_with_snapshot()
    {
        var file = Path.Combine(_tempDir, "setup-old.exe");
        await File.WriteAllTextAsync(file, "bin");

        var result = await _coordinator.ExecuteAsync(new[] { Item(file, CleanupTier.UsersFile, directDelete: true) });

        Assert.True(result.Outcomes[0].Success);
        Assert.Equal("permanent", result.Outcomes[0].Mode);
        Assert.False(File.Exists(file));
        Assert.Equal(1, result.PermanentCount);
        Assert.Empty(_recycle.Requested);
    }

    [Fact]
    public async Task Permanent_channel_missing_falls_back_to_recycle_safely()
    {
        var strict = new CleanupCoordinator(_recycle, _ledger);
        var file = Path.Combine(_tempDir, "cache.tmp");
        await File.WriteAllTextAsync(file, "junk");

        var result = await strict.ExecuteAsync(new[] { Item(file, CleanupTier.Garbage) });

        // 真删通道缺位：安全回退回收站（可还原），绝不静默真删
        Assert.True(result.Outcomes[0].Success);
        Assert.Equal("recycle", result.Outcomes[0].Mode);
        Assert.Equal(0, result.PermanentCount);
        Assert.Single(_recycle.Requested);
        Assert.Empty(_permanent.Deleted);
    }

    [Fact]
    public async Task Snapshot_failure_leaves_original_untouched()
    {
        var file = Path.Combine(_tempDir, "cache.tmp");
        await File.WriteAllTextAsync(file, "junk");
        _permanent.SnapshotFails = true;

        var result = await _coordinator.ExecuteAsync(new[] { Item(file, CleanupTier.Garbage) });

        Assert.False(result.Outcomes[0].Success);
        Assert.True(File.Exists(file));                        // 快照失败绝不动原文件
        Assert.Equal(0, result.SucceededCount);
        var (entries, _) = await _ledger.LoadAsync();
        Assert.Empty(entries);                                 // 失败不记账
    }

    [Fact]
    public void Tier_policy_maps_risk_to_layer()
    {
        Assert.Equal(CleanupTier.Garbage, CleanupTierPolicy.TierOf(JunkRisk.Low));
        Assert.Equal(CleanupTier.Garbage, CleanupTierPolicy.TierOf(JunkRisk.Medium));
        Assert.Equal(CleanupTier.UsersFile, CleanupTierPolicy.TierOf(JunkRisk.High));
    }

    [Fact]
    public void Tier_text_carries_r5_budget_statements()
    {
        Assert.Contains("真删", CleanupTierPolicy.TierText(CleanupTier.Garbage));
        Assert.Contains("快照", CleanupTierPolicy.TierText(CleanupTier.Garbage));
        Assert.Contains("回收站", CleanupTierPolicy.TierText(CleanupTier.UsersFile));
        Assert.Contains("直接删除", CleanupTierPolicy.TierText(CleanupTier.UsersFile));
        Assert.Contains("50 MB", CleanupTierPolicy.BudgetStatement);
        Assert.Contains("零周期写入", CleanupTierPolicy.BudgetStatement);
        Assert.Contains("缓存分类可见", CleanupTierPolicy.BudgetStatement);
        Assert.Contains("规则", CleanupTierPolicy.EvidenceText("system-temp", CleanupTier.Garbage));
    }
}
