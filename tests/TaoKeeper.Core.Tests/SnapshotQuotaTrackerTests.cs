using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Modules.SnapshotChain;
using Xunit;

namespace TaoKeeper.Core.Tests;

public sealed class SnapshotQuotaTrackerTests
{
    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "tk-p1-quota", Guid.NewGuid().ToString("N"));

    private sealed class FakeStore : ISnapshotChainStore
    {
        private readonly Dictionary<string, JournalEntry> _intents = [];
        public void Seed(JournalEntry e) => _intents[e.TransactionId] = e;
        public HashSet<string> Done { get; } = [];
        public HashSet<string> RolledBack { get; } = [];

        public Task WriteIntentAsync(string transactionId, JournalEntry entry, CancellationToken ct)
        {
            _intents[transactionId] = entry;
            return Task.CompletedTask;
        }
        public Task<JournalEntry?> ReadIntentAsync(string transactionId, CancellationToken ct) =>
            Task.FromResult(_intents.TryGetValue(transactionId, out var e) ? e : null);
        public Task WritePayloadAsync(string transactionId, SnapshotItem item, string contentHash, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<(string ContentHash, byte[] Content)>> ReadAllPayloadAsync(string transactionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(string, byte[])>>([]);
        public Task WriteDoneMarkerAsync(string transactionId, CancellationToken ct) { Done.Add(transactionId); return Task.CompletedTask; }
        public Task<bool> HasDoneMarkerAsync(string transactionId, CancellationToken ct) => Task.FromResult(Done.Contains(transactionId));
        public Task WriteRollbackMarkerAsync(string transactionId, CancellationToken ct) { RolledBack.Add(transactionId); return Task.CompletedTask; }
        public Task<bool> HasRollbackMarkerAsync(string transactionId, CancellationToken ct) => Task.FromResult(RolledBack.Contains(transactionId));
        public Task<IReadOnlyList<string>> ListTransactionIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([.. _intents.Keys]);
        public Task<long> LoadSequenceHighWaterAsync(CancellationToken ct) => Task.FromResult(0L);
        public Task SaveSequenceHighWaterAsync(long value, CancellationToken ct) => Task.CompletedTask;
        public Task OverwritePayloadInPlaceAsync(string transactionId, string contentHash, byte[] tampered, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> DeletePayloadAsync(string transactionId, string contentHash, CancellationToken ct) => Task.FromResult(true);
        public Task OverwriteIntentAsync(string transactionId, string rawJson, CancellationToken ct) => Task.CompletedTask;
    }

    private static JournalEntry Entry(long seq, string actionType = "cleanup", string? id = null) =>
        new()
        {
            TransactionId = id ?? $"{seq:D20}-{Guid.NewGuid():N}",
            ActionType = actionType,
            SequenceNumber = seq,
            FileTimeUtc = DateTimeOffset.UtcNow.ToFileTime(),
        };

    private static SnapshotQuotaTracker Make(FakeStore store, Dictionary<string, long> sizes) =>
        new(
            store,
            id => sizes.TryGetValue(id, out var s) ? s : 0,
            id => store.Done.Contains(id) || store.RolledBack.Contains(id));

    [Fact]
    public void R_P16_1_总量与单快照默认值溯源()
    {
        Assert.Equal(5L * 1024 * 1024 * 1024, SnapshotPolicy.DefaultQuotaBytes);
        Assert.Equal(500L * 1024 * 1024, SnapshotPolicy.SingleSnapshotMaxBytes);
        Assert.Equal(7, SnapshotPolicy.DefaultShredRetentionDays);
    }

    [Fact]
    public void A16_1_滚动淘汰最旧已完结_未完结不触碰()
    {
        var store = new FakeStore();
        var e1 = Entry(1); var e2 = Entry(2); var e3 = Entry(3); var pend = Entry(4, id: "pending-4");
        store.Seed(e1); store.Seed(e2); store.Seed(e3); store.Seed(pend);
        store.Done.Add(e1.TransactionId);
        store.Done.Add(e2.TransactionId);
        store.Done.Add(e3.TransactionId);
        // pending 无标记：未完结

        var sizes = new Dictionary<string, long>
        {
            [e1.TransactionId] = 2_000_000_000, [e2.TransactionId] = 2_000_000_000,
            [e3.TransactionId] = 1_000_000_000, [pend.TransactionId] = 100,
        };
        var tracker = Make(store, sizes);
        foreach (var kv in sizes) tracker.Register(kv.Key, kv.Value);

        // 新快照 500MB 进来，配额 5e9B：总 5.5e9+100B 超限，按序淘汰 e1
        var deleted = new List<string>();
        var result = tracker.EvictToQuota(5_000_000_000, e3.TransactionId, id => deleted.Add(id));
        // e3 是 incoming，永不淘汰；pending 未完结，不触碰
        Assert.DoesNotContain(e3.TransactionId, deleted);
        Assert.DoesNotContain(pend.TransactionId, deleted);
        Assert.Equal([e1.TransactionId], result.EvictedTransactionIds);
        Assert.Equal(2_000_000_000, result.FreedBytes);
        Assert.DoesNotContain(e1.TransactionId, tracker.Ledger().Select(l => l.TransactionId));
    }

    [Fact]
    public void A16_1_无完结可淘汰时守住未完结_超配额如实()
    {
        var store = new FakeStore();
        var pend = Entry(1, id: "only-pending");
        store.Seed(pend);
        var sizes = new Dictionary<string, long> { [pend.TransactionId] = SnapshotPolicy.DefaultQuotaBytes + 1 };
        var tracker = Make(store, sizes);
        tracker.Register(pend.TransactionId, sizes[pend.TransactionId]);

        var deleted = new List<string>();
        var result = tracker.EvictToQuota(SnapshotPolicy.DefaultQuotaBytes, pend.TransactionId, id => deleted.Add(id));
        Assert.Empty(deleted); // 未完结不触碰：宁可如实超配额
        Assert.Equal(SnapshotPolicy.DefaultQuotaBytes + 1, tracker.TotalBytes);
    }

    [Fact]
    public void R_P16_1_粉碎快照到期判定_独立保留期()
    {
        var created = new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
        var now = created.AddDays(6.9);
        Assert.False(SnapshotQuotaTracker.IsShredExpired(created, 7, now));
        Assert.True(SnapshotQuotaTracker.IsShredExpired(created, 7, created.AddDays(7)));
    }

    [Fact]
    public void 清单含粉碎类标记_统一清点()
    {
        var store = new FakeStore();
        var shred = Entry(1, actionType: "shred.file");
        var normal = Entry(2, actionType: "cleanup");
        store.Seed(shred); store.Seed(normal);
        var sizes = new Dictionary<string, long> { [shred.TransactionId] = 10, [normal.TransactionId] = 20 };
        var tracker = Make(store, sizes);
        tracker.Register(shred.TransactionId, 10);
        tracker.Register(normal.TransactionId, 20);

        var ledger = tracker.Ledger();
        Assert.Equal(2, ledger.Count);
        Assert.True(ledger.First(l => l.TransactionId == shred.TransactionId).IsShred);
        Assert.False(ledger.First(l => l.TransactionId == normal.TransactionId).IsShred);
        Assert.Equal(30, tracker.TotalBytes);
    }
}
