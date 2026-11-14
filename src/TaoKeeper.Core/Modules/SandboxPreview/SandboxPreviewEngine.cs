using TaoKeeper.Core.Modules.SnapshotChain;

namespace TaoKeeper.Core.Modules.SandboxPreview;

/// <summary>沙箱预演写拦截记录：越界写入被拒绝并逐条上报（R-P13-4，永不静默）。</summary>
public sealed record PreviewInterception(
    DateTimeOffset TimeUtc,
    string TargetPath,
    string Reason);

/// <summary>预演三出口（R-P13-2 语义逐字）：留下=正式执行；丢弃=整体销毁；转正式快照=冻结独立快照。</summary>
public enum PreviewOutcome
{
    /// <summary>留下：登记进快照链新事务，正式执行全部暂存写入。</summary>
    Commit,
    /// <summary>丢弃：整体销毁回滚，仅留「预演已丢弃」日志，零残留。</summary>
    Discard,
    /// <summary>转正式快照：原样冻结为独立快照（含全部拦截记录）。</summary>
    FreezeAsSnapshot,
}

/// <summary>三出口执行回执：每出口带真实状态流（确认→进行中→完成/失败）。</summary>
public sealed record PreviewRunResult(
    PreviewOutcome Outcome,
    bool Succeeded,
    string? Error = null);

/// <summary>
/// 沙箱文件系统抽象：Core 层零真实 I/O，测试注入内存假实现；真机由适配器接真实文件系统（W3 写穿零发生标真机验证项）。
/// </summary>
public interface ISandboxFileSystem
{
    bool Exists(string path);
    byte[] ReadAllBytes(string path);
    void WriteAllBytes(string path, byte[] content);
    void Delete(string path);
    System.Collections.ObjectModel.ReadOnlyCollection<string> WrittenPaths { get; }
    System.Collections.ObjectModel.ReadOnlyCollection<string> DeletedPaths { get; }
}

/// <summary>
/// 越界判定接口：预演期对每个写入目标判定是否越界（越界=拒绝并记录，宁误拦不写穿，R-P13-4）。
/// 真机由适配器注入实际边界（安装目录/系统目录白名单等）；测试注入确定性假判定。
/// </summary>
public interface ISandboxBoundary
{
    /// <summary>返回 null=允许；返回字符串=拒绝原因（逐字上报，不静默）。</summary>
    string? InspectWrite(string path);
}

/// <summary>
/// 沙箱预演引擎（P13 先试后行，R-P13-1~4 落码）。
/// 生命周期：Begin（暂存全部写入，越界拒绝并记录）→ Commit / Discard / Freeze 三出口。
/// 确认执行前自动经快照链拍快照（R-P13-3），快照在 P16 页可见可还原。
/// 丢弃零残留：仅留「预演已丢弃」日志条目（R-P13-2 逐字口径）。
/// </summary>
public sealed class SandboxPreviewEngine
{
    private readonly ISandboxFileSystem _fs;
    private readonly ISandboxBoundary _boundary;
    private readonly ISnapshotChain _chain;
    private readonly List<PreviewInterception> _interceptions = [];
    private readonly Dictionary<string, byte[]> _staged = [];
    private readonly List<string> _stagedDeletes = [];
    private readonly List<(SnapshotHandle Handle, string Label)> _frozen = [];

    public SandboxPreviewEngine(ISandboxFileSystem fileSystem, ISandboxBoundary boundary, ISnapshotChain chain)
    {
        _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        _chain = chain ?? throw new ArgumentNullException(nameof(chain));
    }

    /// <summary>预演期逐条拦截上报（越界 0 次时为空清单，如实显示 0，不造记录）。</summary>
    public IReadOnlyList<PreviewInterception> Interceptions => _interceptions;

    /// <summary>越界计数（UI 逐条展开用；时间+目标路径+拦截原因）。</summary>
    public int ViolationCount => _interceptions.Count;

    /// <summary>转正式快照冻结产物（P16 页可见）。</summary>
    public IReadOnlyList<(SnapshotHandle Handle, string Label)> FrozenSnapshots => _frozen;

    /// <summary>开始预演：此后所有写入进暂存区，越界写入拒绝并记录。</summary>
    public void Begin()
    {
        _interceptions.Clear();
        _staged.Clear();
        _stagedDeletes.Clear();
    }

    /// <summary>预演写：越界→拒绝并记录（返回 false）；未越界→进暂存（不落真实盘）。</summary>
    public bool StageWrite(string path, byte[] content)
    {
        var reason = _boundary.InspectWrite(path);
        if (reason is not null)
        {
            _interceptions.Add(new PreviewInterception(DateTimeOffset.UtcNow, path, reason));
            return false;
        }

        _staged[path] = content;
        return true;
    }

    /// <summary>预演删：进暂存删除清单（不落真实盘）。</summary>
    public void StageDelete(string path) => _stagedDeletes.Add(path);

    /// <summary>
    /// 留下（R-P13-2）：确认执行前自动经快照链拍快照（R-P13-3）；拍失败即中止，不执行任何真实写入（契约第七节）。
    /// 成功后按原路径应用全部暂存写入/删除（正式执行）。
    /// </summary>
    public async Task<PreviewRunResult> CommitAsync(string actionLabel, CancellationToken ct = default)
    {
        var paths = _staged.Keys.ToList();
        var payload = paths
            .Select(p => new SnapshotItem(p, _staged[p]))
            .ToList();
        var size = payload.Sum(i => (long)i.Content.Length);
        if (size > SnapshotPolicy.SingleSnapshotMaxBytes)
        {
            return new PreviewRunResult(PreviewOutcome.Commit, false, SnapshotPolicy.OversizeRefusalMessage);
        }

        SnapshotHandle handle;
        try
        {
            handle = await _chain.CaptureAsync(
                new SnapshotRequest("sandbox-preview.commit", [actionLabel], payload), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new PreviewRunResult(PreviewOutcome.Commit, false, $"快照失败，已中止：{ex.Message}");
        }

        try
        {
            foreach (var (path, content) in _staged)
            {
                _fs.WriteAllBytes(path, content);
            }
            foreach (var path in _stagedDeletes)
            {
                if (_fs.Exists(path)) _fs.Delete(path);
            }
            return new PreviewRunResult(PreviewOutcome.Commit, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 正式执行失败：回滚到快照（快照已先行落盘，可还原）
            await _chain.RestoreAsync(handle, ct).ConfigureAwait(false);
            return new PreviewRunResult(PreviewOutcome.Commit, false, $"正式执行失败，已还原：{ex.Message}");
        }
    }

    /// <summary>丢弃（R-P13-2 逐字）：整体销毁回滚、仅留「预演已丢弃」日志、零残留可断言。</summary>
    public PreviewRunResult Discard()
    {
        _staged.Clear();
        _stagedDeletes.Clear();
        return new PreviewRunResult(PreviewOutcome.Discard, true);
    }

    /// <summary>转正式快照（R-P13-2）：原样冻结独立快照，含全部拦截记录（记录进 payload 摘要字段）。</summary>
    public async Task<PreviewRunResult> FreezeAsSnapshotAsync(string label, CancellationToken ct = default)
    {
        try
        {
            var summary = string.Join('\n', _interceptions.Select(i => $"{i.TimeUtc:O}|{i.TargetPath}|{i.Reason}"));
            var items = new List<SnapshotItem>();
            foreach (var (path, content) in _staged)
            {
                items.Add(new SnapshotItem(path, content));
            }
            items.Add(new SnapshotItem("preview://interceptions.log", System.Text.Encoding.UTF8.GetBytes(summary)));
            var size = items.Sum(i => (long)i.Content.Length);
            if (size > SnapshotPolicy.SingleSnapshotMaxBytes)
            {
                return new PreviewRunResult(PreviewOutcome.FreezeAsSnapshot, false, SnapshotPolicy.OversizeRefusalMessage);
            }

            var handle = await _chain.CaptureAsync(
                new SnapshotRequest("sandbox-preview.freeze", [label], items), ct).ConfigureAwait(false);
            _frozen.Add((handle, label));
            return new PreviewRunResult(PreviewOutcome.FreezeAsSnapshot, true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new PreviewRunResult(PreviewOutcome.FreezeAsSnapshot, false, $"冻结失败：{ex.Message}");
        }
    }
}
