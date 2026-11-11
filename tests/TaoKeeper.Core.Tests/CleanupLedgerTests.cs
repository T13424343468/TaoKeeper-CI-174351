using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>清理账：留痕、封顶逐出、损坏自检、还原标记。</summary>
public class CleanupLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taokeeper-ledger-" + Guid.NewGuid().ToString("N"));
    private string LedgerPath => Path.Combine(_dir, "cleanup-ledger.jsonl");

    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static LedgerEntry E(int i, string source = "junk", long size = 100)
        => new($"e{i}", Base.AddSeconds(i), source, $"/tmp/f{i}", size,
            LedgerRestoreKind.RecycleBin, $"/recycle/f{i}", "rule-x");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task 追加与读回一致()
    {
        var ledger = new CleanupLedger(LedgerPath);
        await ledger.AppendAsync(new[] { E(1), E(2), E(3) });

        var (entries, corrupt) = await ledger.LoadAsync();
        Assert.Equal(3, entries.Count);
        Assert.Equal(0, corrupt);
        Assert.All(entries, e => Assert.True(e.IsRestorable));
    }

    [Fact]
    public async Task 汇总统计正确()
    {
        var ledger = new CleanupLedger(LedgerPath);
        await ledger.AppendAsync(new[] { E(1, size: 1024), E(2, size: 2048), E(3, size: 4096) });
        var s = await ledger.SummarizeAsync();
        Assert.Equal(3, s.TotalEntries);
        Assert.Equal(7168, s.TotalBytes);
        Assert.Equal(3, s.RestorableCount);
        Assert.Equal(0, s.CorruptLinesDropped);
        Assert.NotNull(s.FirstAtUtc);
        Assert.Equal(s.FirstAtUtc!.Value.AddSeconds(2), s.LastAtUtc);
    }

    [Fact]
    public async Task 容量封顶最旧逐出()
    {
        var ledger = new CleanupLedger(LedgerPath) { MaxEntries = 5 };
        await ledger.AppendAsync(Enumerable.Range(1, 8).Select(i => E(i)));

        var (entries, _) = await ledger.LoadAsync();
        Assert.Equal(5, entries.Count);
        Assert.DoesNotContain(entries, e => e.EntryId == "e1"); // 最旧的没了
        Assert.Contains(entries, e => e.EntryId == "e8");
    }

    [Fact]
    public async Task 损坏行自检剔除且其余保留()
    {
        Directory.CreateDirectory(_dir);
        await File.AppendAllTextAsync(LedgerPath, "not-a-json-line\n");
        var good = E(1);
        await File.AppendAllTextAsync(LedgerPath, "{\"entryId\":\"e1\",\"atUtc\":\"2026-11-07T10:00:00Z\",\"source\":\"junk\",\"originalPath\":\"/f\",\"sizeBytes\":10,\"restoreKind\":0,\"binPath\":\"/r\"}\n");

        var ledger = new CleanupLedger(LedgerPath);
        var (entries, corrupt) = await ledger.LoadAsync();
        Assert.Single(entries);
        Assert.Equal(1, corrupt);

        var s = await ledger.SummarizeAsync();
        Assert.Equal(1, s.CorruptLinesDropped); // 如实报告，不静默吞
    }

    [Fact]
    public async Task 按来源过滤()
    {
        var ledger = new CleanupLedger(LedgerPath);
        await ledger.AppendAsync(new[] { E(1, "junk"), E(2, "duplicates"), E(3, "junk") });

        var junkOnly = await ledger.QueryAsync(source: "junk");
        Assert.Equal(2, junkOnly.Count);
        Assert.All(junkOnly, e => Assert.Equal("junk", e.Source));
    }

    [Fact]
    public async Task 标记已还原后不再可还原()
    {
        var ledger = new CleanupLedger(LedgerPath);
        await ledger.AppendAsync(new[] { E(1), E(2) });

        Assert.True(await ledger.MarkRestoredAsync("e1"));
        var (entries, _) = await ledger.LoadAsync();
        var restored = entries.Single(e => e.EntryId == "e1");
        Assert.False(restored.IsRestorable);
        Assert.Null(restored.BinPath);
        Assert.True(entries.Single(e => e.EntryId == "e2").IsRestorable);

        Assert.False(await ledger.MarkRestoredAsync("不存在")); // 未知 id 返回 false
    }
}
