using System.Text.Json.Serialization;

namespace TaoKeeper.Core.Modules.SnapshotChain;

/// <summary>
/// W1 契约冻结件（契约冻结/快照链契约.md 第七节）落码。
/// 时间戳：UTC 100ns FileTime；序列号：模块内单调递增，不回退不复用。
/// </summary>
public readonly record struct ChainTimestamp(long FileTimeUtc, long SequenceNumber);

/// <summary>
/// 快照请求：危险动作执行前提交。动作失败（抛错）时调用方必须中止动作（契约第七节错误语义）。
/// </summary>
public sealed record SnapshotRequest(
    string ActionType,
    IReadOnlyList<string> AffectedObjects,
    IReadOnlyList<SnapshotItem> PayloadItems);

/// <summary>快照条目：名称为目标路径（回滚时按原路径应用），内容为字节。</summary>
public sealed record SnapshotItem(string Name, byte[] Content);

/// <summary>快照句柄：事务 ID + 签发时序 + 内容哈希清单（供 Verify 复核）。</summary>
public sealed record SnapshotHandle(
    string TransactionId,
    ChainTimestamp Timestamp,
    IReadOnlyDictionary<string, string> ContentHashesByPath);

/// <summary>还原结果：成功 / 失败（失败保留 WAL 未决记录等待恢复，不静默）。</summary>
public sealed record RestoreResult(bool Succeeded, string? FailureReason = null)
{
    public static RestoreResult Ok() => new(true);
    public static RestoreResult Failed(string reason) => new(false, reason);
}

/// <summary>恢复报告：断电后收敛结论（契约第四节二态收敛 + 需人工确认）。</summary>
public sealed record RecoveryReport(
    RecoveryOutcome Outcome,
    IReadOnlyList<string> RolledBackTransactionIds,
    IReadOnlyList<string> ManualConfirmTransactionIds);

public enum RecoveryOutcome
{
    /// <summary>无未决记录：状态=事务后。</summary>
    NoPending,
    /// <summary>有未决记录且快照完整：已回放回滚，状态=事务前。</summary>
    RestoredToBefore,
    /// <summary>有未决记录但快照损坏/缺失：拒绝自动恢复，需人工确认（不静默丢弃，N1）。</summary>
    NeedsManualConfirmation,
}

/// <summary>WAL 事务意图：先记意图再执行（契约第三节）。</summary>
public sealed record TransactionIntent(string ActionType, string Description);

/// <summary>
/// WAL 账目条目：intent.json 落盘内容（schemaVersion=1）。
/// 回滚所需数据 = 载荷清单（内容哈希命名，SHA-256）。
/// </summary>
public sealed record JournalEntry
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("transactionId")]
    public string TransactionId { get; init; } = string.Empty;

    [JsonPropertyName("actionType")]
    public string ActionType { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("affectedObjects")]
    public IReadOnlyList<string> AffectedObjects { get; init; } = [];

    [JsonPropertyName("fileTimeUtc")]
    public long FileTimeUtc { get; init; }

    [JsonPropertyName("sequenceNumber")]
    public long SequenceNumber { get; init; }

    [JsonPropertyName("payloadHashes")]
    public IReadOnlyDictionary<string, string> PayloadHashesByPath { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>L6 断电/强杀注入场景（契约第四节，W2 交付，测试探针复用）。</summary>
public enum FaultScenario
{
    /// <summary>意图落盘后、真实写入完成前进程消失（无完成标记）。</summary>
    CrashAfterIntentWrite,
    /// <summary>回滚中途再次断电：部分载荷已应用、无回滚标记（幂等重放须收敛）。</summary>
    CrashMidRollback,
    /// <summary>载荷文件内容被篡改（哈希失配）。</summary>
    CorruptPayload,
    /// <summary>载荷文件整体丢失。</summary>
    DeletePayload,
    /// <summary>intent.json 被写为高版本未知 schema（须拒绝处理，契约第六节）。</summary>
    CorruptIntentSchema,
}

/// <summary>
/// 存储抽象：目录位置由调用方注入（契约第三节第 3 条），模块自身不硬编码路径。
/// Overwrite/Delete 类成员仅供 L6 断电注入（IRecoveryProbe）与测试使用，正常流程不调用。
/// </summary>
public interface ISnapshotChainStore
{
    Task WriteIntentAsync(string transactionId, JournalEntry entry, CancellationToken ct);
    Task<JournalEntry?> ReadIntentAsync(string transactionId, CancellationToken ct);
    Task WritePayloadAsync(string transactionId, SnapshotItem item, string contentHash, CancellationToken ct);

    /// <summary>读取事务载荷：返回（内容哈希=文件名, 字节）清单。</summary>
    Task<IReadOnlyList<(string ContentHash, byte[] Content)>> ReadAllPayloadAsync(string transactionId, CancellationToken ct);
    Task WriteDoneMarkerAsync(string transactionId, CancellationToken ct);
    Task<bool> HasDoneMarkerAsync(string transactionId, CancellationToken ct);
    Task WriteRollbackMarkerAsync(string transactionId, CancellationToken ct);
    Task<bool> HasRollbackMarkerAsync(string transactionId, CancellationToken ct);
    Task<IReadOnlyList<string>> ListTransactionIdsAsync(CancellationToken ct);
    Task<long> LoadSequenceHighWaterAsync(CancellationToken ct);
    Task SaveSequenceHighWaterAsync(long value, CancellationToken ct);

    // —— L6 注入与测试专用（正常流程不调用）——
    Task OverwritePayloadInPlaceAsync(string transactionId, string contentHash, byte[] tampered, CancellationToken ct);
    Task<bool> DeletePayloadAsync(string transactionId, string contentHash, CancellationToken ct);
    Task OverwriteIntentAsync(string transactionId, string rawJson, CancellationToken ct);
}

/// <summary>真实状态应用点：回滚/还原时把快照字节按原路径写回（注入，Core 不直碰真实系统）。</summary>
public interface ISnapshotStateApplier
{
    Task ApplyAsync(string targetPath, byte[] content, CancellationToken ct);
}

/// <summary>契约第七节冻结接口：快照链。</summary>
public interface ISnapshotChain
{
    /// <summary>危险动作执行前调用；失败（返回 false/抛错）时调用方必须中止动作。</summary>
    Task<SnapshotHandle> CaptureAsync(SnapshotRequest request, CancellationToken ct);

    /// <summary>整块还原到指定快照；还原本身走 WAL 事务，崩溃后遵守二态收敛。</summary>
    Task<RestoreResult> RestoreAsync(SnapshotHandle handle, CancellationToken ct);

    /// <summary>快照完整性自检（哈希+结构），供 L8 回滚与实效验收复用。</summary>
    Task<bool> VerifyAsync(SnapshotHandle handle, CancellationToken ct);

    /// <summary>断点恢复：扫描 WAL 并按第四节算法收敛到事务前/事务后二态。</summary>
    Task<RecoveryReport> RecoverPendingAsync(CancellationToken ct);
}

/// <summary>契约第七节冻结接口：WAL 账目。</summary>
public interface ITransactionJournal
{
    /// <summary>先记意图：落盘成功后才允许执行真实写入。</summary>
    Task<JournalEntry> BeginAsync(TransactionIntent intent, CancellationToken ct);

    /// <summary>执行成功后写完成标记。</summary>
    Task MarkCompletedAsync(JournalEntry entry, CancellationToken ct);

    /// <summary>按 WAL 意图回放回滚；幂等（按事务 ID 去重）。</summary>
    Task RollbackAsync(JournalEntry entry, CancellationToken ct);
}

/// <summary>契约第七节冻结接口：L6 断电/强杀注入验证。</summary>
public interface IRecoveryProbe
{
    Task InjectFaultAsync(FaultScenario scenario, CancellationToken ct);
}
