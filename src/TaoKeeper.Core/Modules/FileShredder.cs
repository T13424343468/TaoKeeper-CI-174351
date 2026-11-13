using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaoKeeper.Core.Modules;

/// <summary>粉碎计划条目：Allowed=false 时 DeniedReason 说明为何不可选（选择器标灰口径，N6）。</summary>
public sealed record ShredItem(
    string FullPath,
    long SizeBytes,
    bool Allowed,
    string? DeniedReason = null);

/// <summary>粉碎快照元数据（粉碎前必落盘；还原与到期清除的事实源）。</summary>
public sealed record ShredSnapshotRecord(
    string SnapshotId,
    string OriginalPath,
    long SizeBytes,
    DateTimeOffset CreatedUtc,
    int RetentionDays)
{
    /// <summary>到期时刻（创建 + 保留期；R-P10-4a 默认 7 天可配置）。</summary>
    public DateTimeOffset ExpiresUtc => CreatedUtc.AddDays(RetentionDays);
}

/// <summary>单条粉碎结果（成功=原文件已碎且快照可还原；失败=原状未动，Error 如实）。</summary>
public sealed record ShredOutcome(
    string FullPath,
    long SizeBytes,
    bool Shredded,
    string? SnapshotId = null,
    string? Error = null);

/// <summary>一次粉碎运行回执（R-P10-4b：个数与释放空间，数字实测一致）。</summary>
public sealed record ShredRunResult(
    int ShreddedCount,
    long FreedBytes,
    IReadOnlyList<ShredOutcome> Outcomes);

/// <summary>
/// 文件粉碎引擎（可移植逻辑层，UI 框架零依赖）。
/// 红线 N1 落法：永久删除只发生在「快照先落盘成功」之后——快照复制失败该条不碎、原状保留；
/// 用户显式确认（页面确认弹窗）先于本引擎被调用。粉碎=覆写+删除；到期物理清除走
/// PurgeExpired（全系统唯一合法永久清除路径），还原走 Restore（快照字节回拷原路径）。
/// 覆写为单遍零写（默认，可用 passes 调整）；本引擎不做进度欺骗——每条完成才计入回执。
/// </summary>
public sealed class FileShredder
{
    /// <summary>快照保留天数（默认 7，R-P10-4a 口径；可配置）。</summary>
    public int RetentionDays { get; }

    /// <summary>覆写遍数（默认 1；≥1）。</summary>
    public int OverwritePasses { get; }

    private readonly string _snapshotRoot;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public FileShredder(string snapshotRoot, int retentionDays = 7, int overwritePasses = 1)
    {
        if (string.IsNullOrWhiteSpace(snapshotRoot)) throw new ArgumentException("快照根目录不得为空", nameof(snapshotRoot));
        if (retentionDays <= 0) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        if (overwritePasses < 1) throw new ArgumentOutOfRangeException(nameof(overwritePasses));
        _snapshotRoot = snapshotRoot;
        RetentionDays = retentionDays;
        OverwritePasses = overwritePasses;
    }

    /// <summary>
    /// 系统关键路径判定（N6：选择器中标灰不可选）：等于任一关键根，或位于关键根之下（含盘符根）。
    /// 比较用 OrdinalIgnoreCase，按完整路径逐段对齐，不按子串误判（taokeeper 不命中 "keep" 同理）。
    /// </summary>
    public static bool IsSystemCritical(string fullPath, IReadOnlyCollection<string> criticalRoots)
    {
        if (criticalRoots.Count == 0) return false;
        var normalized = NormalizePath(fullPath);
        foreach (var root in criticalRoots)
        {
            var candidate = NormalizePath(root);
            if (candidate.Length == 0) continue;
            if (normalized.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.StartsWith(candidate.EndsWith(Path.DirectorySeparatorChar)
                    ? candidate
                    : candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>路径规整（双分隔符兼容，按段对齐判定；不改变用户输入本身）。</summary>
    private static string NormalizePath(string path) => path.TrimEnd('/', '\\');

    /// <summary>
    /// 建粉碎计划：存在性、文件/目录、关键目录逐项校验，不满足项 Denied 如实说明（不静默丢）。
    /// </summary>
    public IReadOnlyList<ShredItem> BuildPlan(
        IEnumerable<string> paths, IReadOnlyCollection<string>? criticalRoots = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var roots = criticalRoots ?? Array.Empty<string>();
        var plan = new List<ShredItem>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var full = Path.GetFullPath(raw);
                if (IsSystemCritical(full, roots))
                {
                    plan.Add(new ShredItem(full, 0, Allowed: false, "系统关键目录内的文件不可粉碎（N6）"));
                    continue;
                }
                if (Directory.Exists(full))
                {
                    plan.Add(new ShredItem(full, 0, Allowed: false, "目录不支持粉碎，请选择具体文件"));
                    continue;
                }
                var fi = new FileInfo(full);
                if (!fi.Exists)
                {
                    plan.Add(new ShredItem(full, 0, Allowed: false, "文件不存在（可能已被移动或删除）"));
                    continue;
                }
                plan.Add(new ShredItem(full, fi.Length, Allowed: true));
            }
            catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException
                or IOException or NotSupportedException)
            {
                plan.Add(new ShredItem(raw, 0, Allowed: false, $"路径不可用：{ex.Message}"));
            }
        }
        return plan;
    }

    /// <summary>
    /// 执行粉碎（R-P10-4b）：逐条「快照先落盘 → 覆写 → 删除」，完成一条计一条；
    /// 取消=已碎保持已碎、未处理保持原状（零半截态）；快照失败该条不碎并如实报错。
    /// </summary>
    public async Task<ShredRunResult> ShredAsync(
        IReadOnlyList<ShredItem> plan,
        IProgress<(int Index, string FullPath)>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var outcomes = new List<ShredOutcome>();
        long freed = 0;
        for (var i = 0; i < plan.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var item = plan[i];
            if (!item.Allowed)
            {
                outcomes.Add(new ShredOutcome(item.FullPath, item.SizeBytes, Shredded: false, Error: item.DeniedReason));
                continue;
            }

            progress?.Report((i, item.FullPath));
            outcomes.Add(await ShredOneAsync(item, cancellation).ConfigureAwait(false));
            if (outcomes[^1].Shredded) freed += item.SizeBytes;
        }
        var shredded = outcomes.Count(o => o.Shredded);
        return new ShredRunResult(shredded, freed, outcomes);
    }

    private async Task<ShredOutcome> ShredOneAsync(ShredItem item, CancellationToken cancellation)
    {
        try
        {
            var snapshotId = await WriteSnapshotAsync(item, cancellation).ConfigureAwait(false);
            OverwriteAndDelete(item.FullPath);
            return new ShredOutcome(item.FullPath, item.SizeBytes, Shredded: true, SnapshotId: snapshotId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or NotSupportedException)
        {
            return new ShredOutcome(item.FullPath, item.SizeBytes, Shredded: false,
                Error: $"未粉碎（原状保留）：{ex.Message}");
        }
    }

    /// <summary>快照先落盘：payload 复制 + meta 原子写（tmp+move 家法）；失败抛出=该条不碎。</summary>
    private async Task<string> WriteSnapshotAsync(ShredItem item, CancellationToken cancellation)
    {
        var snapshotId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var dir = Path.Combine(_snapshotRoot, snapshotId);
        Directory.CreateDirectory(dir);

        var payloadPath = Path.Combine(dir, "payload.bin");
        await Task.Run(() => File.Copy(item.FullPath, payloadPath, overwrite: true), cancellation)
            .ConfigureAwait(false);

        var record = new ShredSnapshotRecord(snapshotId, item.FullPath, item.SizeBytes,
            DateTimeOffset.UtcNow, RetentionDays);
        var metaPath = Path.Combine(dir, "meta.json");
        var tmpPath = metaPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(record, _json), cancellation)
            .ConfigureAwait(false);
        File.Move(tmpPath, metaPath, overwrite: true);
        return snapshotId;
    }

    private void OverwriteAndDelete(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        var length = fi.Length;
        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Seek(0, SeekOrigin.Begin);
            var zero = new byte[64 * 1024];
            for (var pass = 0; pass < OverwritePasses; pass++)
            {
                fs.Seek(0, SeekOrigin.Begin);
                var remaining = length;
                while (remaining > 0)
                {
                    var n = (int)Math.Min(zero.Length, remaining);
                    fs.Write(zero, 0, n);
                    remaining -= n;
                }
                fs.Flush(true); // 落盘到介质，不做内存假覆写
            }
        }
        fi.Delete();
    }

    /// <summary>全部快照（meta 损坏的目录跳过并如实标注，不假数据）。</summary>
    public IReadOnlyList<ShredSnapshotRecord> ListSnapshots()
    {
        if (!Directory.Exists(_snapshotRoot)) return Array.Empty<ShredSnapshotRecord>();
        var list = new List<ShredSnapshotRecord>();
        foreach (var dir in Directory.GetDirectories(_snapshotRoot))
        {
            var meta = Path.Combine(dir, "meta.json");
            if (!File.Exists(meta)) continue;
            try
            {
                list.Add(JsonSerializer.Deserialize<ShredSnapshotRecord>(File.ReadAllText(meta), _json)
                    ?? throw new JsonException());
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // 损坏快照跳过（如实不读，不参与还原/到期判定）
            }
        }
        return list.OrderByDescending(r => r.CreatedUtc).ToList();
    }

    /// <summary>已过保留期的快照（到期物理清除对象；R-P10-4b）。</summary>
    public IReadOnlyList<ShredSnapshotRecord> ListExpired(DateTimeOffset nowUtc) =>
        ListSnapshots().Where(r => r.ExpiresUtc <= nowUtc).ToList();

    /// <summary>
    /// 到期物理清除（全系统唯一合法永久清除路径）：删除过期快照目录（payload+meta）。
    /// 清除动作本身由页面二次确认后调用；返回实际清除的快照数。
    /// </summary>
    public int PurgeExpired(DateTimeOffset nowUtc)
    {
        var purged = 0;
        foreach (var record in ListExpired(nowUtc))
        {
            var dir = Path.Combine(_snapshotRoot, record.SnapshotId);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                purged++;
            }
        }
        return purged;
    }

    /// <summary>从快照还原：payload 字节回拷原路径（快照保留，可再还原）；原路径所在目录不在则先建。</summary>
    public void Restore(string snapshotId)
    {
        var dir = Path.Combine(_snapshotRoot, snapshotId);
        var meta = Path.Combine(dir, "meta.json");
        if (!File.Exists(meta)) throw new InvalidOperationException("快照不存在或元数据损坏，无法还原");
        var record = JsonSerializer.Deserialize<ShredSnapshotRecord>(File.ReadAllText(meta), _json)
            ?? throw new InvalidOperationException("快照元数据为空，无法还原");
        var payload = Path.Combine(dir, "payload.bin");
        if (!File.Exists(payload)) throw new InvalidOperationException("快照载荷缺失，无法还原");

        var targetDir = Path.GetDirectoryName(record.OriginalPath);
        if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
        File.Copy(payload, record.OriginalPath, overwrite: true);
    }
}
