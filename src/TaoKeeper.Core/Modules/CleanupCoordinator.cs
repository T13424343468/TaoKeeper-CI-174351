using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TaoKeeper.Core.Modules;

/// <summary>一条待执行的清理动作（来自任意模块的确认项）。</summary>
public sealed record CleanupActionItem(
    /// <summary>目标完整路径：文件或空目录。</summary>
    string FullPath,
    /// <summary>该路径的体积（字节），记账用。</summary>
    long SizeBytes,
    /// <summary>发起来源模块：junk / duplicates / storage-sense / widget / toolbox / manual。</summary>
    string Source,
    /// <summary>用户逐条确认；false 时编排层直接拒执行，绝不代删。</summary>
    bool Confirmed,
    /// <summary>命中规则或任务名，透传记账。</summary>
    string? RuleId = null,
    /// <summary>分层语义（0.2）：确定垃圾默认真删，用户文件默认回收站。</summary>
    CleanupTier Tier = CleanupTier.Garbage,
    /// <summary>用户文件层勾选「直接删除（跳过回收站）」时为 true；删前同样自动留快照。</summary>
    bool DirectDelete = false);

/// <summary>单条执行结果。</summary>
public sealed record CleanupOutcomeItem(
    string FullPath,
    /// <summary>true=已移入回收站/备份区并记账；false=未动目标。</summary>
    bool Success,
    /// <summary>回收站/备份中的实际落点（成功时）。</summary>
    string? BinPath,
    /// <summary>失败原因（拒绝执行或适配层报错时）。</summary>
    string? Error = null,
    /// <summary>该条目分层（回执展示用；未标注按确定垃圾口径）。</summary>
    CleanupTier? Tier = null,
    /// <summary>执行通道：recycle（回收站）或 permanent（快照后真删）；未执行为 null。</summary>
    string? Mode = null);

/// <summary>一批清理执行的汇总。</summary>
public sealed record CleanupBatchResult(
    IReadOnlyList<CleanupOutcomeItem> Outcomes,
    int SucceededCount,
    long SucceededBytes,
    int RefusedCount,
    int FailedCount,
    /// <summary>真删成功条数（D-1 ②「空间实际增加」以真删类统计）。</summary>
    int PermanentCount = 0,
    /// <summary>真删成功合计字节（=磁盘可用空间的实际增量口径）。</summary>
    long PermanentBytes = 0);

/// <summary>
/// 文件移除适配器：把文件或空目录移入回收站/备份区，返回实际落点（供还原）。
/// 沙箱用内存假适配器实测编排；真机接 Win32 回收站 API 或备份目录实现。
/// </summary>
public interface IFileRemovalAdapter
{
    /// <summary>移走一个文件或空目录；成功返回其在回收站/备份中的新路径，失败返回 null。</summary>
    Task<string?> MoveToRecycleBinAsync(string fullPath, CancellationToken cancellation = default);
}

/// <summary>
/// 真删适配器：删前自动留快照（经快照链/备份目录，期内可还原），快照成功后才物理删除原文件。
/// 铁律：快照失败绝不动原文件；返回的 SnapshotPath 即清理账中的可还原落点。
/// </summary>
public interface IPermanentRemovalAdapter
{
    /// <summary>快照先行再删除一个文件或目录；快照或删除失败时原文件不动，返回失败与原因。</summary>
    Task<PermanentRemovalResult> SnapshotThenDeleteAsync(string fullPath, CancellationToken cancellation = default);
}

/// <summary>一次真删的结果（快照落点 = 清理账可还原路径）。</summary>
public sealed record PermanentRemovalResult(
    bool Success,
    /// <summary>快照在快照区中的实际落点（成功时非空；期内可还原）。</summary>
    string? SnapshotPath = null,
    string? Error = null);

/// <summary>
/// 清理执行编排（垃圾清理/重复文件/工具箱/挂件共用的最后一段）：
/// 逐条确认闸门 → 受保护路径硬拒绝（不触碰系统关键目录，用户确认也不放行）→
/// 分层语义执行（0.2 拍板）——确定垃圾类与勾选「直接删除」的用户文件走快照先行真删通道，
/// 用户文件默认走适配层移动到回收站/备份 → 清理账记账（含 BinPath 供还原）。
/// 铁律：零静默删除——未确认项不执行；真删必先快照，快照通道未接线时如实拒绝不降级；
/// 失败逐条报告不中断批次；本类自己不做任何 IO 移动。
/// </summary>
public sealed class CleanupCoordinator
{
    private readonly IFileRemovalAdapter _adapter;
    private readonly CleanupLedger _ledger;
    private readonly IPermanentRemovalAdapter? _permanentRemoval;

    /// <summary>受保护目录名（大小写不敏感）：命中任何路径段的项一律硬拒绝。</summary>
    public ISet<string> ProtectedDirectoryNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData",
            "$RECYCLE.BIN", "System Volume Information"
        };

    /// <summary>真删通道未接线的降级说明（回执可见：安全回退回收站，可还原、不静默真删）。</summary>
    public const string PermanentChannelMissingMessage = "真删通道未接线，已安全回退回收站（可还原）；真机接线后确定垃圾将默认真删（先快照）。";

    public CleanupCoordinator(IFileRemovalAdapter adapter, CleanupLedger ledger,
        IPermanentRemovalAdapter? permanentRemoval = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _permanentRemoval = permanentRemoval;
    }

    /// <summary>执行一批已确认的清理动作。返回逐条结果与汇总；账本只记成功移走的条目。</summary>
    public async Task<CleanupBatchResult> ExecuteAsync(
        IReadOnlyList<CleanupActionItem> items, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var outcomes = new List<CleanupOutcomeItem>(items.Count);
        var ledgerEntries = new List<LedgerEntry>();
        long succeededBytes = 0;
        long permanentBytes = 0;
        int permanentCount = 0;

        foreach (var item in items)
        {
            cancellation.ThrowIfCancellationRequested();
            var refusal = RefuseReason(item, ProtectedDirectoryNames);
            if (refusal is not null)
            {
                outcomes.Add(new CleanupOutcomeItem(item.FullPath, false, null, refusal));
                continue;
            }

            // 分层语义（0.2）：确定垃圾类默认真删；用户文件默认回收站，勾选「直接删除」后同样真删（先快照）
            var usePermanent = item.Tier == CleanupTier.Garbage || item.DirectDelete;
            if (usePermanent && _permanentRemoval is not null)
            {
                PermanentRemovalResult removal;
                try
                {
                    removal = await _permanentRemoval.SnapshotThenDeleteAsync(item.FullPath, cancellation);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                              or System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    removal = new PermanentRemovalResult(false, null, $"快照/删除失败：{ex.Message}");
                }

                if (removal.Success)
                {
                    outcomes.Add(new CleanupOutcomeItem(item.FullPath, true, removal.SnapshotPath,
                        null, item.Tier, "permanent"));
                    succeededBytes += item.SizeBytes;
                    permanentBytes += item.SizeBytes;
                    permanentCount++;
                    // 快照落点即清理账可还原路径：真删条目在快照保留期内可还原（0.2 与 N1 收窄口径）
                    ledgerEntries.Add(new LedgerEntry(
                        EntryId: Guid.NewGuid().ToString("N"),
                        AtUtc: DateTimeOffset.UtcNow,
                        Source: item.Source,
                        OriginalPath: item.FullPath,
                        SizeBytes: item.SizeBytes,
                        RestoreKind: LedgerRestoreKind.TrashDir,
                        BinPath: removal.SnapshotPath,
                        RuleId: item.RuleId));
                }
                else
                {
                    outcomes.Add(new CleanupOutcomeItem(item.FullPath, false, null,
                        removal.Error ?? "快照/删除失败：适配器未返回结果", item.Tier, null));
                }
                continue;
            }

            string? binPath = null;
            string? error = null;
            try
            {
                binPath = await _adapter.MoveToRecycleBinAsync(item.FullPath, cancellation);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                error = $"移动失败：{ex.Message}";
            }
            if (binPath is null && error is null)
                error = "移动失败：适配器未返回落点";

            if (binPath is not null)
            {
                outcomes.Add(new CleanupOutcomeItem(item.FullPath, true, binPath, null, item.Tier, "recycle"));
                succeededBytes += item.SizeBytes;
                ledgerEntries.Add(new LedgerEntry(
                    EntryId: Guid.NewGuid().ToString("N"),
                    AtUtc: DateTimeOffset.UtcNow,
                    Source: item.Source,
                    OriginalPath: item.FullPath,
                    SizeBytes: item.SizeBytes,
                    RestoreKind: LedgerRestoreKind.RecycleBin,
                    BinPath: binPath,
                    RuleId: item.RuleId));
            }
            else
            {
                outcomes.Add(new CleanupOutcomeItem(item.FullPath, false, null,
                    error ?? "移动失败：未知原因", item.Tier, null));
            }
        }

        if (ledgerEntries.Count > 0)
            await _ledger.AppendAsync(ledgerEntries, cancellation);

        var succeeded = outcomes.Where(o => o.Success).ToList();
        return new CleanupBatchResult(
            outcomes,
            succeeded.Count,
            succeededBytes,
            outcomes.Count(o => o.Error?.StartsWith("拒绝") == true),
            outcomes.Count(o => !o.Success && o.Error?.StartsWith("拒绝") != true),
            permanentCount,
            permanentBytes);
    }

    /// <summary>硬拒绝理由：null=可执行；判定统一走 CleanupSafetyGate（与界面预检同一事实源）。</summary>
    private static string? RefuseReason(CleanupActionItem item, ISet<string> protectedNames) =>
        CleanupSafetyGate.Evaluate(item.FullPath, item.Confirmed, protectedNames);
}
