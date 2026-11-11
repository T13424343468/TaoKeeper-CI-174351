using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// 清理执行编排测试：逐条确认闸门、受保护路径硬拒绝、盘符根拒绝、
/// 成功记账（含落点供还原）、单条失败不中断批次。零静默删除为硬要求。
/// </summary>
public class CleanupCoordinatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CleanupLedger _ledger;
    private readonly FakeRemovalAdapter _adapter = new();
    private readonly CleanupCoordinator _coordinator;

    public CleanupCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tk-cleanup-" + Guid.NewGuid().ToString("N"));
        _ledger = new CleanupLedger(Path.Combine(_tempDir, "ledger.jsonl"));
        _coordinator = new CleanupCoordinator(_adapter, _ledger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    /// <summary>内存假适配器：记录被移动的路径，按预设抛错模拟移动失败。</summary>
    private sealed class FakeRemovalAdapter : IFileRemovalAdapter
    {
        public List<string> Requested { get; } = new();
        public Func<string, bool>? ShouldFail { get; set; }

        public Task<string?> MoveToRecycleBinAsync(string fullPath, CancellationToken cancellation = default)
        {
            Requested.Add(fullPath);
            if (ShouldFail?.Invoke(fullPath) == true)
                throw new InvalidOperationException("模拟移动失败");
            return Task.FromResult<string?>($@"C:\$Recycle.Bin\{Path.GetFileName(fullPath)}");
        }
    }

    private static CleanupActionItem Item(string path, bool confirmed = true, long size = 100,
        string source = "junk") => new(path, size, source, confirmed);

    [Fact]
    public async Task 未确认项_绝不执行也不记账()
    {
        var result = await _coordinator.ExecuteAsync(new[] { Item(@"D:\下载\临时.log", confirmed: false) });

        Assert.Empty(_adapter.Requested);                 // 适配器根本没被调
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(0, result.SucceededBytes);
        Assert.Equal(0, result.FailedCount);
        Assert.Single(result.Outcomes);                   // 逐条给出拒绝理由，不留空白
        Assert.StartsWith("拒绝", result.Outcomes[0].Error);
        var summary = await _ledger.SummarizeAsync();
        Assert.Equal(0, summary.TotalEntries);            // 账本零新增
    }

    [Fact]
    public async Task 受保护路径与盘符根_确认了也硬拒绝()
    {
        var items = new[]
        {
            Item(@"C:\Windows\Temp\x.log"),               // 受保护段
            Item(@"C:\ProgramData\y.dat"),                // 受保护段
            Item(@"C:\"),                                  // 盘符根
            Item(@"D:\下载\确认文件.tmp"),                 // 正常条目
        };
        var result = await _coordinator.ExecuteAsync(items);

        Assert.Equal(1, result.SucceededCount);           // 只有正常条目被执行
        Assert.Single(_adapter.Requested);
        Assert.Equal(@"D:\下载\确认文件.tmp", _adapter.Requested[0]);
        Assert.Equal(3, result.RefusedCount);             // 两保护一改根
        Assert.Equal(100, result.SucceededBytes);
    }

    [Fact]
    public async Task 成功执行_逐条记账含还原落点()
    {
        var result = await _coordinator.ExecuteAsync(new[]
        {
            Item(@"D:\缓存\a.tmp", size: 2048, source: "junk", confirmed: true),
            Item(@"D:\缓存\子\b.tmp", size: 1, source: "toolbox", confirmed: true),
        });

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(2049, result.SucceededBytes);
        Assert.All(result.Outcomes, o =>
        {
            Assert.True(o.Success);
            Assert.NotNull(o.BinPath);                    // 落点回传，还原有据
        });
        var entries = (await _ledger.LoadAsync()).Entries;
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.True(e.IsRestorable);                  // 记账条目均带还原落点
            Assert.Equal(LedgerRestoreKind.RecycleBin, e.RestoreKind);
        });
        Assert.Equal(2048, entries.First(e => e.OriginalPath == @"D:\缓存\a.tmp").SizeBytes);
    }

    [Fact]
    public async Task 单条失败_不中断批次且如实入账零新增()
    {
        _adapter.ShouldFail = p => p.EndsWith("b.tmp"); // b.tmp 触发假适配器抛错
        var result = await _coordinator.ExecuteAsync(new[]
        {
            Item(@"D:\缓存\a.tmp", size: 10),
            Item(@"D:\缓存\b.tmp", size: 20),
            Item(@"D:\缓存\c.tmp", size: 30),
        });

        Assert.Equal(2, result.SucceededCount);           // a、c 照常成功
        Assert.Equal(1, result.FailedCount);
        var failed = result.Outcomes.First(o => o.FullPath == @"D:\缓存\b.tmp");
        Assert.False(failed.Success);
        Assert.StartsWith("移动失败", failed.Error);      // 失败原因逐条可见
        var summary = await _ledger.SummarizeAsync();
        Assert.Equal(2, summary.TotalEntries);            // 失败条目绝不入账
    }

    [Fact]
    public async Task 取消令牌触发_批次中止并抛出()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _coordinator.ExecuteAsync(new[] { Item(@"D:\缓存\a.tmp") }, cts.Token));
        Assert.Empty(_adapter.Requested);
    }
}
