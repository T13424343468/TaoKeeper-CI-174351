using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Models;

namespace TaoKeeper.Core.Modules;

/// <summary>路径构建结果：完整路径映射 + 构建失败（父链缺失/成环/自指）回退记录号清单。</summary>
public sealed record MftPathBuildResult(
    IReadOnlyDictionary<ulong, string> FullPaths,
    IReadOnlyList<ulong> FallbackRecordNos);

/// <summary>
/// MFT 记录号→完整路径重建（纯托管，衔接 MftStreamingScanner 产出的紧凑条目；
/// 语义与 native BuildFullPath 对齐：父链自底向上拼「\」，根记录号（NTFS 卷根默认 5）名不拼入，
/// 父链缺失/父子互指成环/自指该链全部拒绝，交调用方退回裸名）。
/// 同记录号重复时首个生效（MFT 正常不重复，防御口径）；带记忆化的批量为常用入口，单条查询走同一路径。
/// 非线程安全（内部缓存不可并发写）。
/// </summary>
public sealed class MftPathBuilder
{
    private readonly Dictionary<ulong, MftEntryInfo> _byRecord;
    private readonly Dictionary<ulong, string?> _memo; // null=该链已判失败
    private readonly ulong _rootRecord;

    public MftPathBuilder(IEnumerable<MftEntryInfo> entries, ulong rootRecord = 5)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _rootRecord = rootRecord;
        _byRecord = new Dictionary<ulong, MftEntryInfo>();
        foreach (var e in entries)
            if (!_byRecord.ContainsKey(e.RecordNo))
                _byRecord[e.RecordNo] = e;
        _memo = new Dictionary<ulong, string?>();
    }

    /// <summary>批量构建全部记录的完整路径（成功入映射，失败进回退清单，均按记录号升序）。</summary>
    public MftPathBuildResult BuildAll()
    {
        var paths = new Dictionary<ulong, string>();
        var fallback = new List<ulong>();
        foreach (var recordNo in _byRecord.Keys.OrderBy(k => k))
        {
            if (TryBuildFullPath(recordNo, out var path))
                paths[recordNo] = path;
            else
                fallback.Add(recordNo);
        }
        return new MftPathBuildResult(paths, fallback);
    }

    /// <summary>构建单条完整路径；父链缺失/成环/自指返回 false（调用方退回裸名）。</summary>
    public bool TryBuildFullPath(ulong recordNo, out string fullPath)
    {
        fullPath = "";
        if (_memo.TryGetValue(recordNo, out var cached))
        {
            if (cached is null) return false;
            fullPath = cached;
            return true;
        }
        if (recordNo == _rootRecord)
        {
            // 根自身：路径为空串（根名不拼入）；根不在映射时同 native 判失败
            if (!_byRecord.ContainsKey(recordNo))
            {
                _memo[recordNo] = null;
                return false;
            }
            _memo[recordNo] = "";
            return true;
        }
        if (!_byRecord.ContainsKey(recordNo))
        {
            _memo[recordNo] = null;
            return false;
        }

        var chain = new List<(ulong RecordNo, string Name)>();
        var visited = new HashSet<ulong>(); // 链上逐点登记；起点自指会在第二次 Add 时判环
        var cur = recordNo;
        string resolvedPrefix;
        while (true)
        {
            if (cur == _rootRecord)
            {
                resolvedPrefix = "";
                break;
            }
            if (_memo.TryGetValue(cur, out var cachedAncestor))
            {
                if (cachedAncestor is null)
                {
                    MemoChainFailed(chain);
                    return false; // 命中已判失败的祖先链（失败沿链传播）
                }
                resolvedPrefix = cachedAncestor;
                break;
            }
            if (!_byRecord.TryGetValue(cur, out var entry))
            {
                MemoChainFailed(chain);
                return false; // 父链缺失
            }
            if (!visited.Add(cur))
            {
                MemoChainFailed(chain);
                return false; // 父子互指成环（自指同判）
            }
            chain.Add((cur, entry.Name));
            cur = entry.ParentRecordNo;
        }

        if (chain.Count == 0) // 起点即根或命中记忆化，直接回填
        {
            fullPath = resolvedPrefix;
            _memo[recordNo] = fullPath;
            return true;
        }

        // 链上逐点回填：chain 自底向上（chain[0]=起点，越往后越靠祖先），
        // path(chain[i]) = 前缀 + chain[i..0] 的名字按祖先→后代顺序拼接
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var parts = new List<string>();
            for (var j = i; j >= 0; j--) parts.Add(chain[j].Name); // chain[i](祖先)→chain[0](起点)
            var path = string.Join("\\", parts);
            path = resolvedPrefix.Length == 0 ? path : resolvedPrefix + "\\" + path;
            _memo[chain[i].RecordNo] = path;
        }
        fullPath = _memo[recordNo]!;
        return true;
    }

    private void MemoChainFailed(List<(ulong RecordNo, string Name)> chain)
    {
        foreach (var (recordNo, _) in chain)
            _memo[recordNo] = null;
    }
}
