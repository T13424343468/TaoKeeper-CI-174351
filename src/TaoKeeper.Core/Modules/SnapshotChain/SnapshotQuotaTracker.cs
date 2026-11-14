namespace TaoKeeper.Core.Modules.SnapshotChain;

/// <summary>快照清单条目（P16 页展示：时间本地时区、动作、对象数、占用字节、是否粉碎类）。</summary>
public sealed record SnapshotLedgerEntry(
    string TransactionId,
    ChainTimestamp Timestamp,
    string ActionType,
    int ObjectCount,
    long SizeBytes,
    bool IsShred);

/// <summary>淘汰回执：淘汰了哪些事务、释放字节（未完结事务零触碰，引擎口径 A16-3）。</summary>
public sealed record EvictionResult(IReadOnlyList<string> EvictedTransactionIds, long FreedBytes);

/// <summary>
/// 快照总量滚动管理（R-P16-1 落码）：5GB 默认滚动可配、单快照 500MB 硬上限、
/// 粉碎类快照独立保留期（默认 7 天，P16 页统一清点、到期物理清除前二次确认由页面收口）。
/// 滚动淘汰只碰「已完成」事务（done/rollback 标记），未完结事务不触碰（A16-3，引擎口径）。
/// </summary>
public sealed class SnapshotQuotaTracker
{
    private readonly ISnapshotChainStore _store;
    private readonly Func<string, long> _sizeOf;
    private readonly Func<string, bool> _isCompleted;

    /// <summary>snapshotId → 字节（调用方登记；P16 统一清点的内存视图）。</summary>
    private readonly Dictionary<string, long> _sizes = [];

    /// <summary>sizeOf=按事务 ID 算字节；isCompleted=该事务是否已有完成/回滚标记（未完结不淘汰）。</summary>
    public SnapshotQuotaTracker(ISnapshotChainStore store, Func<string, long> sizeOf, Func<string, bool> isCompleted)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sizeOf = sizeOf ?? throw new ArgumentNullException(nameof(sizeOf));
        _isCompleted = isCompleted ?? throw new ArgumentNullException(nameof(isCompleted));
    }

    /// <summary>登记一笔快照占用（拍快照成功后调用；超单快照上限由调用方先拒）。</summary>
    public void Register(string transactionId, long sizeBytes) => _sizes[transactionId] = sizeBytes;

    /// <summary>当前总占用（P16 页读数：总占用可见可清）。</summary>
    public long TotalBytes => _sizes.Values.Sum();

    /// <summary>清单（按时间升序；粉碎类=actionType 以 shred 开头）。</summary>
    public IReadOnlyList<SnapshotLedgerEntry> Ledger()
    {
        var list = new List<SnapshotLedgerEntry>();
        foreach (var (id, size) in _sizes)
        {
            var entry = _store.ReadIntentAsync(id, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            if (entry is null) continue;
            list.Add(new SnapshotLedgerEntry(
                id,
                new ChainTimestamp(entry.FileTimeUtc, entry.SequenceNumber),
                entry.ActionType,
                entry.AffectedObjects.Count,
                size,
                IsShred: entry.ActionType.StartsWith("shred", StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    /// <summary>
    /// 滚动淘汰：总量含新快照后超配额时，从最旧已完结事务开始淘汰，直到不超（A16-1/R-P16-1）。
    /// 新快照自身与未完结事务永不淘汰（A16-3）；淘汰动作=删目录（物理清除，P16 页确认后调用）。
    /// </summary>
    public EvictionResult EvictToQuota(long quotaBytes, string incomingId, Action<string> deleteSnapshot)
    {
        ArgumentNullException.ThrowIfNull(deleteSnapshot);
        var freed = new List<string>();
        long freedBytes = 0;
        if (!_sizes.TryGetValue(incomingId, out var incomingSize)) incomingSize = 0;

        while (TotalBytes > quotaBytes)
        {
            var victim = _sizes
                .Where(kv => kv.Key != incomingId && _isCompleted(kv.Key))
                .OrderBy(kv => _store.ReadIntentAsync(kv.Key, CancellationToken.None)
                    .ConfigureAwait(false).GetAwaiter().GetResult()?.SequenceNumber ?? long.MaxValue)
                .FirstOrDefault();
            if (victim.Key is null) break; // 无可淘汰（全是不完结或仅剩新快照）：守住未完结红线，超配额如实报
            deleteSnapshot(victim.Key);
            freedBytes += victim.Value;
            _sizes.Remove(victim.Key);
            freed.Add(victim.Key);
        }
        return new EvictionResult(freed, freedBytes);
    }

    /// <summary>粉碎类快照到期判定（独立保留期）：到期=创建 + 保留期 ≤ now（R-P16-1；页面二次确认后调物理清除）。</summary>
    public static bool IsShredExpired(DateTimeOffset createdUtc, int retentionDays, DateTimeOffset nowUtc) =>
        createdUtc.AddDays(retentionDays) <= nowUtc;
}
