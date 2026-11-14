namespace TaoKeeper.Core.Modules.ResidueDetect;

/// <summary>引用图节点类型（R-P14-1：注册表项/文件/快捷方式/计划任务/服务）。</summary>
public enum ResidueNodeType
{
    RegistryKey,
    File,
    Shortcut,
    ScheduledTask,
    Service,
}

/// <summary>引用图节点：类型 + 路径/键名 + 目标（快捷方式与服务指向的真实目标，判「目标缺失」用）。</summary>
public sealed record ResidueNode(
    ResidueNodeType Type,
    string Path,
    string? Target = null,
    DateTimeOffset? LastModifiedUtc = null);

/// <summary>
/// 有向边：from 引用 to（如快捷方式指向文件、服务指向二进制、注册表卸载串指向安装目录）。
/// </summary>
public sealed record ResidueEdge(string FromPath, string ToPath);

/// <summary>
/// 节点源抽象：真机由适配器枚举注册表/文件/快捷方式/计划任务/服务（只读）；
/// 沙箱注入内存对象图（假节点适配器，Core 层全测口径）。
/// </summary>
public interface IResidueNodeSource
{
    IReadOnlyList<ResidueNode> Nodes { get; }
    IReadOnlyList<ResidueEdge> Edges { get; }
}

/// <summary>残留判定结果：候选（零引用）与待确认分离展示（R-P14-1 原型 flowPending 口径）。</summary>
public sealed record ResidueReport(
    IReadOnlyList<ResidueNode> CleanupCandidates,
    IReadOnlyList<ResidueNode> PendingConfirmation);

/// <summary>
/// 残留侦探内核（P14，R-P14-1 落码）：安装根可达性分析。
/// 判定（保守原则 C1：零引用≠零残留，宁保守）：
///   不可达 且 引用目标缺失 → 残留；不可达 且 引用目标存在/无目标 → 待确认组（人工判断：列路径+最近修改时间）。
/// 可达节点不进任何组。候选=不可达且引用目标缺失；待确认=不可达但目标在（或无目标信息），交用户勾选定去留。
/// </summary>
public static class ResidueDetector
{
    /// <summary>从安装根做可达性分析并出残留报告。</summary>
    public static ResidueReport Analyze(IResidueNodeSource source, IEnumerable<string> installRoots)
    {
        ArgumentNullException.ThrowIfNull(source);
        var roots = (installRoots ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 出边邻接表：from → to 列表
        var outEdges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in source.Edges)
        {
            if (!outEdges.TryGetValue(e.FromPath, out var list))
            {
                list = [];
                outEdges[e.FromPath] = list;
            }
            list.Add(e.ToPath);
        }

        // 入度可达集：从安装根沿出边 BFS（可达=仍被根链路引用，不是残留）。
        // 保守原则 C1：安装根自身的子树（路径在根之下）视为可达——根目录本身被引用，其下文件不自动判残留。
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var root in roots)
        {
            if (reachable.Add(root)) queue.Enqueue(root);
        }
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!outEdges.TryGetValue(cur, out var nexts)) continue;
            foreach (var next in nexts)
            {
                if (reachable.Add(next)) queue.Enqueue(next);
            }
        }

        bool UnderRoot(string path) => roots.Any(r =>
            path.StartsWith(r.TrimEnd('\\', '/') + @"\", StringComparison.OrdinalIgnoreCase));
        foreach (var n in source.Nodes)
        {
            if (UnderRoot(n.Path)) reachable.Add(n.Path);
        }

        var candidates = new List<ResidueNode>();
        var pending = new List<ResidueNode>();
        foreach (var node in source.Nodes)
        {
            if (reachable.Contains(node.Path)) continue;
            if (node.Target is not null)
            {
                // 有引用目标：目标存在 → 待确认（引用挂着的对象还在，不自动判残留）；
                // 目标缺失 → 残留候选（A14-1 逐字：「不可达 且 引用目标缺失」）。
                if (node.Type == ResidueNodeType.Shortcut || node.Type == ResidueNodeType.Service
                    || node.Type == ResidueNodeType.ScheduledTask || node.Type == ResidueNodeType.RegistryKey)
                {
                    var targetAlive = source.Nodes.Any(n =>
                        n.Path.Equals(node.Target, StringComparison.OrdinalIgnoreCase))
                        || roots.Contains(node.Target);
                    if (!targetAlive)
                    {
                        candidates.Add(node);
                        continue;
                    }
                    continue; // 目标在：不进候选也不进待确认（引用仍挂有效对象，保守 C1）
                }
            }
            pending.Add(node);
        }

        return new ResidueReport(candidates, pending);
    }
}
