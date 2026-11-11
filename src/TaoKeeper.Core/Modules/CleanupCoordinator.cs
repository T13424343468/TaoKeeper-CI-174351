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
    string? RuleId = null);

/// <summary>单条执行结果。</summary>
public sealed record CleanupOutcomeItem(
    string FullPath,
    /// <summary>true=已移入回收站/备份区并记账；false=未动目标。</summary>
    bool Success,
    /// <summary>回收站/备份中的实际落点（成功时）。</summary>
    string? BinPath,
    /// <summary>失败原因（拒绝执行或适配层报错时）。</summary>
    string? Error = null);

/// <summary>一批清理执行的汇总。</summary>
public sealed record CleanupBatchResult(
    IReadOnlyList<CleanupOutcomeItem> Outcomes,
    int SucceededCount,
    long SucceededBytes,
    int RefusedCount,
    int FailedCount);

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
/// 清理执行编排（垃圾清理/重复文件/工具箱/挂件共用的最后一段）：
/// 逐条确认闸门 → 受保护路径硬拒绝（不触碰系统关键目录，用户确认也不放行）→
/// 适配层移动到回收站/备份 → 清理账记账（含 BinPath 供还原）。
/// 铁律：零静默删除——未确认项不执行；失败逐条报告不中断批次；本类自己不做任何 IO 移动。
/// </summary>
public sealed class CleanupCoordinator
{
    private readonly IFileRemovalAdapter _adapter;
    private readonly CleanupLedger _ledger;

    /// <summary>受保护目录名（大小写不敏感）：命中任何路径段的项一律硬拒绝。</summary>
    public ISet<string> ProtectedDirectoryNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData",
            "$RECYCLE.BIN", "System Volume Information"
        };

    public CleanupCoordinator(IFileRemovalAdapter adapter, CleanupLedger ledger)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    /// <summary>执行一批已确认的清理动作。返回逐条结果与汇总；账本只记成功移走的条目。</summary>
    public async Task<CleanupBatchResult> ExecuteAsync(
        IReadOnlyList<CleanupActionItem> items, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var outcomes = new List<CleanupOutcomeItem>(items.Count);
        var ledgerEntries = new List<LedgerEntry>();
        long succeededBytes = 0;

        foreach (var item in items)
        {
            cancellation.ThrowIfCancellationRequested();
            var refusal = RefuseReason(item, ProtectedDirectoryNames);
            if (refusal is not null)
            {
                outcomes.Add(new CleanupOutcomeItem(item.FullPath, false, null, refusal));
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
                outcomes.Add(new CleanupOutcomeItem(item.FullPath, true, binPath));
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
                    error ?? "移动失败：未知原因"));
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
            outcomes.Count(o => !o.Success && o.Error?.StartsWith("拒绝") != true));
    }

    /// <summary>硬拒绝理由：null=可执行；判定统一走 CleanupSafetyGate（与界面预检同一事实源）。</summary>
    private static string? RefuseReason(CleanupActionItem item, ISet<string> protectedNames) =>
        CleanupSafetyGate.Evaluate(item.FullPath, item.Confirmed, protectedNames);
}
