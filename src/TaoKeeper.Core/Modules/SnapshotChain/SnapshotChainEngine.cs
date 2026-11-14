using System.Text;

namespace TaoKeeper.Core.Modules.SnapshotChain;

/// <summary>
/// 快照链引擎：ISnapshotChain + ITransactionJournal + IRecoveryProbe 的 Core 实现。
/// 线程模型：同一实例内部锁串行化（契约第七节）；跨实例并发由调用方保证互斥。
/// 恢复算法（契约第四节）：无未决→事务后；未决+载荷完整→回放回滚→事务前；未决+载荷损坏/缺失或意图损坏→需人工确认（不静默丢弃，N1）。
/// 回滚幂等：按事务 ID 去重（回滚标记），重放=整块按原路径字节级重写，收敛结论不变。
/// 载荷文件按内容哈希（SHA-256）命名；目标路径由意图清单（path→hash）映射解析。
/// </summary>
public sealed class SnapshotChainEngine : ISnapshotChain, ITransactionJournal, IRecoveryProbe
{
    private readonly ISnapshotChainStore _store;
    private readonly ISnapshotStateApplier _applier;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sequenceHighWater;

    public SnapshotChainEngine(ISnapshotChainStore store, ISnapshotStateApplier applier)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _sequenceHighWater = _store.LoadSequenceHighWaterAsync(CancellationToken.None)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    // ---------- ISnapshotChain ----------

    public async Task<SnapshotHandle> CaptureAsync(SnapshotRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = await WriteIntentLockedAsync(
                request.ActionType, description: string.Empty, request.AffectedObjects, request.PayloadItems, ct)
                .ConfigureAwait(false);
            return new SnapshotHandle(
                entry.TransactionId,
                new ChainTimestamp(entry.FileTimeUtc, entry.SequenceNumber),
                entry.PayloadHashesByPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RestoreResult> RestoreAsync(SnapshotHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = await _store.ReadIntentAsync(handle.TransactionId, ct).ConfigureAwait(false);
            if (entry is null)
            {
                return RestoreResult.Failed($"intent for {handle.TransactionId} not found; refusing restore");
            }

            var payload = await _store.ReadAllPayloadAsync(handle.TransactionId, ct).ConfigureAwait(false);
            if (payload.Count != entry.PayloadHashesByPath.Count)
            {
                return RestoreResult.Failed(
                    $"payload count mismatch for {handle.TransactionId}: have {payload.Count}, manifest {entry.PayloadHashesByPath.Count}");
            }

            if (!PayloadManifestIntact(entry, payload))
            {
                return RestoreResult.Failed($"payload integrity check failed for {handle.TransactionId}; refusing restore");
            }

            await ApplyPayloadLockedAsync(entry, payload, ct).ConfigureAwait(false);
            await _store.WriteDoneMarkerAsync(handle.TransactionId, ct).ConfigureAwait(false);
            return RestoreResult.Ok();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // 失败保留 WAL 未决记录等待恢复（契约第七节错误语义）
            return RestoreResult.Failed($"restore failed for {handle.TransactionId}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> VerifyAsync(SnapshotHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = await _store.ReadIntentAsync(handle.TransactionId, ct).ConfigureAwait(false);
            if (entry is null || entry.SchemaVersion != 1)
            {
                return false;
            }

            if (!entry.PayloadHashesByPath.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .SequenceEqual(handle.ContentHashesByPath.OrderBy(kv => kv.Key, StringComparer.Ordinal)))
            {
                return false;
            }

            var payload = await _store.ReadAllPayloadAsync(entry.TransactionId, ct).ConfigureAwait(false);
            return payload.Count == entry.PayloadHashesByPath.Count
                && PayloadManifestIntact(entry, payload);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecoveryReport> RecoverPendingAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ids = await _store.ListTransactionIdsAsync(ct).ConfigureAwait(false);
            var rolledBack = new List<string>();
            var manualConfirm = new List<string>();

            foreach (var id in ids)
            {
                if (await _store.HasDoneMarkerAsync(id, ct).ConfigureAwait(false)
                    || await _store.HasRollbackMarkerAsync(id, ct).ConfigureAwait(false))
                {
                    continue; // 已收敛（事务后或事务前），不重复处理
                }

                JournalEntry? entry;
                try
                {
                    entry = await _store.ReadIntentAsync(id, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
                {
                    manualConfirm.Add(id); // 意图损坏（含高版本 schema）：拒绝自动恢复
                    continue;
                }

                if (entry is null)
                {
                    manualConfirm.Add(id); // 无意图记录=结构不完整事务
                    continue;
                }

                var payload = await _store.ReadAllPayloadAsync(id, ct).ConfigureAwait(false);
                if (payload.Count != entry.PayloadHashesByPath.Count || !PayloadManifestIntact(entry, payload))
                {
                    manualConfirm.Add(id); // 载荷损坏/缺失：拒绝自动恢复，交上层显式决策（N1）
                    continue;
                }

                await RollbackLockedAsync(entry, payload, ct).ConfigureAwait(false);
                rolledBack.Add(id);
            }

            var outcome = manualConfirm.Count > 0
                ? RecoveryOutcome.NeedsManualConfirmation
                : rolledBack.Count > 0
                    ? RecoveryOutcome.RestoredToBefore
                    : RecoveryOutcome.NoPending;
            return new RecoveryReport(outcome, rolledBack, manualConfirm);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- ITransactionJournal ----------

    public async Task<JournalEntry> BeginAsync(TransactionIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await WriteIntentLockedAsync(intent.ActionType, intent.Description, [], [], ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkCompletedAsync(JournalEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _store.WriteDoneMarkerAsync(entry.TransactionId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RollbackAsync(JournalEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payload = await _store.ReadAllPayloadAsync(entry.TransactionId, ct).ConfigureAwait(false);
            await RollbackLockedAsync(entry, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按事务 ID 读取意图（引擎公共面，供上层恢复决策与验证探针复用）。</summary>
    public async Task<JournalEntry?> ReadIntentAsync(string transactionId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _store.ReadIntentAsync(transactionId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- IRecoveryProbe ----------

    public async Task InjectFaultAsync(FaultScenario scenario, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            const string probePath = @"C:\probe\tk-snapshot-fault.txt";
            var items = new List<SnapshotItem> { new(probePath, Encoding.UTF8.GetBytes("pre-state-bytes")) };
            var entry = await WriteIntentLockedAsync(
                "probe.fault-injection", "L6 fault injection seed", [probePath], items, ct)
                .ConfigureAwait(false);

            switch (scenario)
            {
                case FaultScenario.CrashAfterIntentWrite:
                case FaultScenario.CrashMidRollback:
                    // 意图已落盘、无完成标记：即为未决事务；MidRollback 的部分应用由幂等重放兜底，无需额外动作
                    break;
                case FaultScenario.CorruptPayload:
                    {
                        var hash = entry.PayloadHashesByPath[probePath];
                        await _store.OverwritePayloadInPlaceAsync(
                            entry.TransactionId, hash, Encoding.UTF8.GetBytes("tampered-bytes"), ct).ConfigureAwait(false);
                        break;
                    }
                case FaultScenario.DeletePayload:
                    {
                        var hash = entry.PayloadHashesByPath[probePath];
                        await _store.DeletePayloadAsync(entry.TransactionId, hash, ct).ConfigureAwait(false);
                        break;
                    }
                case FaultScenario.CorruptIntentSchema:
                    await _store.OverwriteIntentAsync(
                        entry.TransactionId, "{\"schemaVersion\":99,\"transactionId\":\"x\"}", ct).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "unknown fault scenario");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- 内部 ----------

    private async Task<JournalEntry> WriteIntentLockedAsync(
        string actionType,
        string description,
        IReadOnlyList<string> affectedObjects,
        IReadOnlyList<SnapshotItem> payloadItems,
        CancellationToken ct)
    {
        var highWater = await _store.LoadSequenceHighWaterAsync(ct).ConfigureAwait(false);
        _sequenceHighWater = Math.Max(_sequenceHighWater, highWater) + 1;
        await _store.SaveSequenceHighWaterAsync(_sequenceHighWater, ct).ConfigureAwait(false);

        var transactionId = $"{_sequenceHighWater:D20}-{Guid.NewGuid():N}";
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in payloadItems)
        {
            hashes[item.Name] = FileSnapshotChainStore.ComputeSha256(item.Content);
        }

        var entry = new JournalEntry
        {
            TransactionId = transactionId,
            ActionType = actionType,
            Description = description,
            AffectedObjects = affectedObjects,
            FileTimeUtc = DateTimeOffset.UtcNow.ToFileTime(),
            SequenceNumber = _sequenceHighWater,
            PayloadHashesByPath = hashes,
        };

        // 先落盘意图，后写载荷（WAL 先记意图，契约第三节第 1 条）
        await _store.WriteIntentAsync(transactionId, entry, ct).ConfigureAwait(false);
        foreach (var item in payloadItems)
        {
            await _store.WritePayloadAsync(transactionId, item, hashes[item.Name], ct).ConfigureAwait(false);
        }

        return entry;
    }

    private async Task RollbackLockedAsync(
        JournalEntry entry, IReadOnlyList<(string ContentHash, byte[] Content)> payload, CancellationToken ct)
    {
        if (!PayloadManifestIntact(entry, payload))
        {
            throw new InvalidOperationException(
                $"rollback refused for {entry.TransactionId}: payload integrity check failed (N1: no silent drop)");
        }

        await ApplyPayloadLockedAsync(entry, payload, ct).ConfigureAwait(false);
        await _store.WriteRollbackMarkerAsync(entry.TransactionId, ct).ConfigureAwait(false);
    }

    private async Task ApplyPayloadLockedAsync(
        JournalEntry entry,
        IReadOnlyList<(string ContentHash, byte[] Content)> payload,
        CancellationToken ct)
    {
        foreach (var (contentHash, content) in payload)
        {
            var targetPath = entry.PayloadHashesByPath.First(kv => kv.Value == contentHash).Key;
            await _applier.ApplyAsync(targetPath, content, ct).ConfigureAwait(false);
        }
    }

    /// <summary>载荷完整性：每个文件的字节数哈希必须等于文件名（内容寻址双向核）。</summary>
    private static bool PayloadManifestIntact(
        JournalEntry entry, IReadOnlyList<(string ContentHash, byte[] Content)> payload)
    {
        foreach (var (contentHash, content) in payload)
        {
            if (!entry.PayloadHashesByPath.Values.Contains(contentHash, StringComparer.Ordinal))
            {
                return false;
            }

            if (FileSnapshotChainStore.ComputeSha256(content) != contentHash)
            {
                return false; // 文件内容被原地篡改（哈希失配）
            }
        }

        return true;
    }
}
