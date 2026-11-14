using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules.ResidueDetect;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>残留清单行：候选（零引用）与待确认分离（R-P14-1 原型 flowPending 口径）。</summary>
public sealed record ResidueRow(string Path, string KindLabel, string? LastModifiedLocal);

/// <summary>
/// P14 残留侦探 VM（引用图）：未扫描=未测态零预填（V-A10）；
/// 待确认组人工判断交互——列路径+最近修改时间，勾选进清理候选、保留项不再提示。
/// </summary>
public sealed class ResidueDetectPageViewModel
{
    public string Phase { get; private set; } = "idle"; // idle=未扫描 / scanned=已扫

    public IReadOnlyList<ResidueRow> CleanupCandidates { get; private set; } = [];
    public IReadOnlyList<ResidueRow> Pending { get; private set; } = [];

    /// <summary>用户勾选「保留」的路径：保留项不再提示。</summary>
    public HashSet<string> KeptPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>保留动作后从待确认移除；勾选进清理候选。</summary>
    public void Keep(string path)
    {
        KeptPaths.Add(path);
        Pending = Pending.Where(p => !p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>节点源注入（App 层接真实注册表/文件枚举适配器；Core 层图算法全测）。</summary>
    public void Load(IResidueNodeSource source, IEnumerable<string> installRoots)
    {
        Phase = "scanned";
        var report = ResidueDetector.Analyze(source, installRoots);
        CleanupCandidates = report.CleanupCandidates.Select(ToRow).ToList();
        Pending = report.PendingConfirmation.Where(p => !KeptPaths.Contains(p.Path)).Select(ToRow).ToList();
    }

    private static ResidueRow ToRow(ResidueNode n) => new(
        Path: n.Path,
        KindLabel: n.Type switch
        {
            ResidueNodeType.RegistryKey => "注册表项",
            ResidueNodeType.File => "文件",
            ResidueNodeType.Shortcut => "快捷方式",
            ResidueNodeType.ScheduledTask => "计划任务",
            ResidueNodeType.Service => "服务",
            _ => "其他",
        },
        LastModifiedLocal: n.LastModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
}
