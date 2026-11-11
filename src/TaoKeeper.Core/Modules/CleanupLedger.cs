using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaoKeeper.Core.Modules;

/// <summary>清理账的一条记录：一次清理动作中一个被移走的文件。</summary>
public sealed record LedgerEntry(
    string EntryId,
    DateTimeOffset AtUtc,
    /// <summary>发起来源模块：junk / duplicates / storage-sense / widget / manual。</summary>
    string Source,
    /// <summary>被清走的文件原始完整路径。</summary>
    string OriginalPath,
    long SizeBytes,
    /// <summary>还原方式：recycle-bin（回收站，附 BinPath）或 trash-dir（备份目录）。</summary>
    LedgerRestoreKind RestoreKind,
    /// <summary>回收站/备份中的当前位置（还原用）。</summary>
    string? BinPath,
    /// <summary>命中规则或清理任务名，如 "system-temp"。</summary>
    string? RuleId = null)
{
    public bool IsRestorable => !string.IsNullOrEmpty(BinPath);
}

/// <summary>还原通道。</summary>
public enum LedgerRestoreKind { RecycleBin, TrashDir }

/// <summary>清理账汇总视图。</summary>
public sealed record LedgerSummary(
    int TotalEntries,
    long TotalBytes,
    DateTimeOffset? FirstAtUtc,
    DateTimeOffset? LastAtUtc,
    int RestorableCount,
    /// <summary>损坏而未能读回的行数（自检发现的）。</summary>
    int CorruptLinesDropped);

/// <summary>
/// 清理账（设置页与各清理模块共用的留痕与还原入口，可移植逻辑层）。
/// 追加写 JSONL；读取时损坏行自检剔除并在汇总中如实报告；
/// 容量封顶 + 最旧先出（LRU 逐出），永不无限增长。
/// 道系边界：账本只记录与还原，不做任何删除动作本身。
/// </summary>
public sealed class CleanupLedger
{
    /// <summary>账本单文件最大行数（默认 5000），超出按最旧逐出。</summary>
    public int MaxEntries { get; init; } = 5000;

    private readonly string _filePath;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public CleanupLedger(string filePath) => _filePath = filePath;

    /// <summary>追加一批清理记录；越界时先做最旧逐出。</summary>
    public async Task AppendAsync(IEnumerable<LedgerEntry> entries, CancellationToken cancellation = default)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        foreach (var e in entries)
            sb.Append(JsonSerializer.Serialize(e, _json)).Append('\n');

        await File.AppendAllTextAsync(_filePath, sb.ToString(), Encoding.UTF8, cancellation);
        await EnforceCapAsync(cancellation);
    }

    /// <summary>读回全部账目；损坏行跳过并计数，绝不因个别坏行丢整本账。</summary>
    public async Task<(IReadOnlyList<LedgerEntry> Entries, int CorruptLines)> LoadAsync(
        CancellationToken cancellation = default)
    {
        var entries = new List<LedgerEntry>();
        int corrupt = 0;
        if (!File.Exists(_filePath)) return (entries, 0);

        foreach (var line in await File.ReadAllLinesAsync(_filePath, Encoding.UTF8, cancellation))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<LedgerEntry>(line, _json);
                if (e is not null && !string.IsNullOrEmpty(e.EntryId)) entries.Add(e);
                else corrupt++;
            }
            catch (JsonException) { corrupt++; }
        }
        return (entries.OrderBy(e => e.AtUtc).ToList(), corrupt);
    }

    /// <summary>汇总视图（含损坏自检结果）。</summary>
    public async Task<LedgerSummary> SummarizeAsync(CancellationToken cancellation = default)
    {
        var (entries, corrupt) = await LoadAsync(cancellation);
        return new LedgerSummary(
            entries.Count,
            entries.Sum(e => e.SizeBytes),
            entries.FirstOrDefault()?.AtUtc,
            entries.LastOrDefault()?.AtUtc,
            entries.Count(e => e.IsRestorable),
            corrupt);
    }

    /// <summary>按来源与时间过滤。</summary>
    public async Task<IReadOnlyList<LedgerEntry>> QueryAsync(
        string? source = null, DateTimeOffset? sinceUtc = null,
        CancellationToken cancellation = default)
    {
        var (entries, _) = await LoadAsync(cancellation);
        return entries
            .Where(e => source is null || string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase))
            .Where(e => sinceUtc is null || e.AtUtc >= sinceUtc)
            .ToList();
    }

    /// <summary>标记某条已还原（还原动作完成后由上层调用，BinPath 清空表示已回到原位）。</summary>
    public async Task<bool> MarkRestoredAsync(string entryId, CancellationToken cancellation = default)
    {
        var (entries, corrupt) = await LoadAsync(cancellation);
        var target = entries.FirstOrDefault(e => e.EntryId == entryId);
        if (target is null || !target.IsRestorable) return false;

        await RewriteAsync(entries.Where(e => e.EntryId != entryId)
            .Append(target with { BinPath = null, RestoreKind = LedgerRestoreKind.TrashDir })
            .OrderBy(e => e.AtUtc), corrupt, cancellation);
        return true;
    }

    /// <summary>容量封顶：超过 MaxEntries 时按最旧逐出。</summary>
    private async Task EnforceCapAsync(CancellationToken cancellation)
    {
        var (entries, corrupt) = await LoadAsync(cancellation);
        if (entries.Count <= MaxEntries) return;
        var kept = entries.OrderBy(e => e.AtUtc).Skip(entries.Count - MaxEntries);
        await RewriteAsync(kept, corrupt, cancellation);
    }

    private async Task RewriteAsync(IEnumerable<LedgerEntry> entries, int corruptLines,
        CancellationToken cancellation)
    {
        var tmp = _filePath + ".tmp";
        var sb = new StringBuilder();
        foreach (var e in entries)
            sb.Append(JsonSerializer.Serialize(e, _json)).Append('\n');
        await File.WriteAllTextAsync(tmp, sb.ToString(), Encoding.UTF8, cancellation);
        File.Move(tmp, _filePath, overwrite: true);
    }
}
