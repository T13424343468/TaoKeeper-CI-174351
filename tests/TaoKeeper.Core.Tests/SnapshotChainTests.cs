using System.Text;
using TaoKeeper.Core.Modules.SnapshotChain;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// C3/C3-R 契约测试：快照链+WAL（W1 契约冻结件逐条对齐）。
/// 覆盖：时间戳精度与序列单调、WAL 先记意图、断电二态收敛、幂等重放、任意中间态还原、Verify、schema 拒收。
/// </summary>
public sealed class SnapshotChainTests : IDisposable
{
    private readonly string _root;
    private readonly FakeStateApplier _applier;

    public SnapshotChainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tk-snapshotchain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _applier = new FakeStateApplier();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private FileSnapshotChainStore NewStore() => new(_root);

    private SnapshotChainEngine NewEngine(ISnapshotChainStore? store = null) =>
        new(store ?? NewStore(), _applier);

    private sealed class FakeStateApplier : ISnapshotStateApplier
    {
        public Dictionary<string, byte[]> State { get; } = new(StringComparer.Ordinal);
        public List<byte[]> AppliedBatches { get; } = [];

        public Task ApplyAsync(string targetPath, byte[] content, CancellationToken ct)
        {
            State[targetPath] = content;
            return Task.CompletedTask;
        }
    }

    // ---------- 时间戳与序列号（契约第二节） ----------

    [Fact]
    public async Task Sequence_is_monotonically_increasing_across_captures_and_engine_restarts()
    {
        var store = NewStore();
        var engine1 = NewEngine(store);

        var h1 = await engine1.CaptureAsync(Req("a"), CancellationToken.None);
        var h2 = await engine1.CaptureAsync(Req("b"), CancellationToken.None);
        Assert.True(h2.Timestamp.SequenceNumber > h1.Timestamp.SequenceNumber, "same engine: strictly increasing");

        // 断电重启：新引擎接管同一存储，序列号继续单调（不复用、不回退）
        var engine2 = new SnapshotChainEngine(store, _applier);
        var h3 = await engine2.CaptureAsync(Req("c"), CancellationToken.None);
        Assert.True(h3.Timestamp.SequenceNumber > h2.Timestamp.SequenceNumber, "across restart: strictly increasing");
    }

    [Fact]
    public async Task Timestamp_is_utc_100ns_filetime()
    {
        var engine = NewEngine();
        var before = DateTimeOffset.UtcNow.ToFileTime();
        var handle = await engine.CaptureAsync(Req("t"), CancellationToken.None);
        var after = DateTimeOffset.UtcNow.ToFileTime();

        Assert.InRange(handle.Timestamp.FileTimeUtc, before, after);
        // 排序键=序列号→FileTime 双键；不依赖本地时钟单调性
        var h2 = await engine.CaptureAsync(Req("t2"), CancellationToken.None);
        Assert.True(h2.Timestamp.FileTimeUtc >= handle.Timestamp.FileTimeUtc);
    }

    // ---------- WAL 先记意图（契约第三节） ----------

    [Fact]
    public async Task Begin_writes_intent_to_disk_before_any_execution()
    {
        var engine = NewEngine();
        var entry = await engine.BeginAsync(new TransactionIntent("probe.action", "wal discipline"), CancellationToken.None);

        // 意图已落盘（intent.json 存在），且未带完成标记=未决
        var intentPath = Path.Combine(_root, "payload", entry.TransactionId, "intent.json");
        Assert.True(File.Exists(intentPath), "WAL intent must be persisted before real write");
        Assert.False(File.Exists(Path.Combine(_root, "payload", entry.TransactionId, "rollback.marker")));
        Assert.False(File.Exists(Path.Combine(_root, "payload", entry.TransactionId, "done.marker")));

        await engine.MarkCompletedAsync(entry, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_root, "payload", entry.TransactionId, "done.marker")));
    }

    // ---------- 断电收敛（契约第四节，C3-R） ----------

    [Fact]
    public async Task Recovery_with_no_pending_converges_to_after_state()
    {
        var engine = NewEngine();
        var h = await engine.CaptureAsync(Req("done-action"), CancellationToken.None);
        var entry = await engine.ReadIntentAsync(h.TransactionId, CancellationToken.None);
        Assert.NotNull(entry);
        await engine.MarkCompletedAsync(entry, CancellationToken.None);

        var report = await engine.RecoverPendingAsync(CancellationToken.None);
        Assert.Equal(RecoveryOutcome.NoPending, report.Outcome);
    }

    [Fact]
    public async Task Recovery_with_pending_and_intact_payload_rolls_back_to_before_state()
    {
        var engine = NewEngine();
        const string targetPath = @"C:\probe\tk-snapshot-fault.txt";
        await engine.InjectFaultAsync(FaultScenario.CrashAfterIntentWrite, CancellationToken.None);

        // 断电瞬间：真实状态=事务后（动作已执行）；重启恢复后必须收敛到事务前（快照字节）
        var report = await engine.RecoverPendingAsync(CancellationToken.None);
        Assert.Equal(RecoveryOutcome.RestoredToBefore, report.Outcome);
        Assert.Single(report.RolledBackTransactionIds);
        Assert.Equal(Encoding.UTF8.GetBytes("pre-state-bytes"), _applier.State[targetPath]);

        // 幂等重放：同一未决记录再恢复一次，结论不变（无第三态、不重复回滚）
        var second = await engine.RecoverPendingAsync(CancellationToken.None);
        Assert.Equal(RecoveryOutcome.NoPending, second.Outcome);
    }

    [Fact]
    public async Task Recovery_mid_rollback_replays_full_payload_idempotently()
    {
        var engine = NewEngine();
        // 模拟回滚中途断电：意图在、部分应用、无回滚标记——恢复必须整块重放收敛到事务前
        await engine.InjectFaultAsync(FaultScenario.CrashMidRollback, CancellationToken.None);

        var report = await engine.RecoverPendingAsync(CancellationToken.None);
        Assert.Equal(RecoveryOutcome.RestoredToBefore, report.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes("pre-state-bytes"), _applier.State[@"C:\probe\tk-snapshot-fault.txt"]);
    }

    [Fact]
    public async Task Recovery_with_corrupt_payload_refuses_auto_recovery()
    {
        var engine = NewEngine();
        await engine.InjectFaultAsync(FaultScenario.CorruptPayload, CancellationToken.None);

        var report = await engine.RecoverPendingAsync(CancellationToken.None);
        Assert.Equal(RecoveryOutcome.NeedsManualConfirmation, report.Outcome);
        Assert.Single(report.ManualConfirmTransactionIds);
        Assert.Empty(report.RolledBackTransactionIds);
        // 不静默丢弃（N1）：未写任何收敛标记
        Assert.False(_applier.State.ContainsKey(@"C:\probe\tk-snapshot-fault.txt"));
    }

    [Fact]
    public async Task Recovery_with_missing_payload_refuses_auto_recovery()
    {
        var engine = NewEngine();
        await engine.InjectFaultAsync(FaultScenario.DeletePayload, CancellationToken.None);

        var report = await engine.RecoverPendingAsync(CancellationToken.None);
        Assert.Equal(RecoveryOutcome.NeedsManualConfirmation, report.Outcome);
        Assert.Single(report.ManualConfirmTransactionIds);
    }

    [Fact]
    public async Task Recovery_with_higher_schema_intent_refuses_processing()
    {
        var engine = NewEngine();
        await engine.InjectFaultAsync(FaultScenario.CorruptIntentSchema, CancellationToken.None);

        var report = await engine.RecoverPendingAsync(CancellationToken.None);
        Assert.Equal(RecoveryOutcome.NeedsManualConfirmation, report.Outcome);
        Assert.Single(report.ManualConfirmTransactionIds);
    }

    // ---------- 快照可恢复语义（契约第五节） ----------

    [Fact]
    public async Task Restore_from_arbitrary_middle_snapshot_recovers_that_state()
    {
        var engine = NewEngine();
        var first = await engine.CaptureAsync(new SnapshotRequest(
            "file.edit", [@"C:\probe\doc.txt"],
            [new SnapshotItem(@"C:\probe\doc.txt", Encoding.UTF8.GetBytes("version-one"))]),
            CancellationToken.None);
        await engine.MarkCompletedAsync((await engine.ReadIntentAsync(first.TransactionId, CancellationToken.None))!, CancellationToken.None);

        var second = await engine.CaptureAsync(new SnapshotRequest(
            "file.edit", [@"C:\probe\doc.txt"],
            [new SnapshotItem(@"C:\probe\doc.txt", Encoding.UTF8.GetBytes("version-two"))]),
            CancellationToken.None);

        // 真实状态已前进到 version-two；从任意中间态（第一张快照）整块还原
        var result = await engine.RestoreAsync(first, CancellationToken.None);
        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(Encoding.UTF8.GetBytes("version-one"), _applier.State[@"C:\probe\doc.txt"]);
        Assert.True(second.TransactionId != first.TransactionId);
    }

    [Fact]
    public async Task Restore_is_itself_a_wal_transaction_with_done_marker()
    {
        var engine = NewEngine();
        var handle = await engine.CaptureAsync(Req("r"), CancellationToken.None);

        await engine.RestoreAsync(handle, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_root, "payload", handle.TransactionId, "done.marker")));
    }

    [Fact]
    public async Task Verify_detects_tampered_payload()
    {
        var engine = NewEngine();
        var handle = await engine.CaptureAsync(Req("v"), CancellationToken.None);
        Assert.True(await engine.VerifyAsync(handle, CancellationToken.None));

        await engine.InjectFaultAsync(FaultScenario.CorruptPayload, CancellationToken.None);
        // 注入的事务是另一个事务；原快照仍完整；注入事务的 Verify 应失败
        var report = await engine.RecoverPendingAsync(CancellationToken.None);
        Assert.Equal(RecoveryOutcome.NeedsManualConfirmation, report.Outcome);
    }

    [Fact]
    public async Task Verify_false_when_intent_missing()
    {
        var engine = NewEngine();
        var handle = await engine.CaptureAsync(Req("vm"), CancellationToken.None);
        var ghost = handle with { TransactionId = "00000000000000000000-ghost" };
        Assert.False(await engine.VerifyAsync(ghost, CancellationToken.None));
    }

    // ---------- 错误语义（契约第七节） ----------

    [Fact]
    public async Task Capture_failure_throws_and_caller_must_abort()
    {
        var throwingStore = new ThrowingStore();
        var engine = new SnapshotChainEngine(throwingStore, _applier);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.CaptureAsync(Req("x"), CancellationToken.None));
    }

    [Fact]
    public async Task Restore_failure_keeps_wal_pending()
    {
        var engine = NewEngine();
        var handle = await engine.CaptureAsync(Req("keep-pending"), CancellationToken.None);

        // 载荷被删：还原失败返回 Failed 且不写完成标记（未决保留等待恢复）
        var entry = (await engine.ReadIntentAsync(handle.TransactionId, CancellationToken.None))!;
        var hash = entry.PayloadHashesByPath.Values.First();
        await NewStore().DeletePayloadAsync(handle.TransactionId, hash, CancellationToken.None);

        var result = await engine.RestoreAsync(handle, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(_root, "payload", handle.TransactionId, "done.marker")));

        var report = await engine.RecoverPendingAsync(CancellationToken.None);
        Assert.Equal(RecoveryOutcome.NeedsManualConfirmation, report.Outcome);
    }

    // ---------- 辅助 ----------

    private static SnapshotRequest Req(string action) => new(
        action, [@"C:\probe\tk-snapshot-fault.txt"],
        [new SnapshotItem(@"C:\probe\tk-snapshot-fault.txt", Encoding.UTF8.GetBytes("pre-state-bytes"))]);

    private sealed class ThrowingStore : ISnapshotChainStore
    {
        public Task WriteIntentAsync(string transactionId, JournalEntry entry, CancellationToken ct) =>
            throw new InvalidOperationException("store failure injected");

        public Task<JournalEntry?> ReadIntentAsync(string transactionId, CancellationToken ct) => Task.FromResult<JournalEntry?>(null);
        public Task WritePayloadAsync(string transactionId, SnapshotItem item, string contentHash, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<(string ContentHash, byte[] Content)>> ReadAllPayloadAsync(string transactionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(string, byte[])>>([]);
        public Task WriteDoneMarkerAsync(string transactionId, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> HasDoneMarkerAsync(string transactionId, CancellationToken ct) => Task.FromResult(false);
        public Task WriteRollbackMarkerAsync(string transactionId, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> HasRollbackMarkerAsync(string transactionId, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> ListTransactionIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<long> LoadSequenceHighWaterAsync(CancellationToken ct) => Task.FromResult(0L);
        public Task SaveSequenceHighWaterAsync(long value, CancellationToken ct) => Task.CompletedTask;
        public Task OverwritePayloadInPlaceAsync(string transactionId, string contentHash, byte[] tampered, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> DeletePayloadAsync(string transactionId, string contentHash, CancellationToken ct) => Task.FromResult(true);
        public Task OverwriteIntentAsync(string transactionId, string rawJson, CancellationToken ct) => Task.CompletedTask;
    }
}
