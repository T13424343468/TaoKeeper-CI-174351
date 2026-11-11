using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>
/// 重复文件页视图模型（UI 框架零依赖、零写动作）：
/// 接 DuplicateFileFinder 的分组结果（零误报原则：仅全文件 SHA-256 完全相同才判重复，本层如实转述不疑）。
/// 每组保留一份、其余默认列为待清理；保留选择为组内单选，改选时原保留份转为待清理；
/// 待清理项经 CleanupQueueViewModel 统一确认闸门入队（受保护路径/盘符根从源头标死不可勾），
/// 回收站语义由执行编排层保证。本模型不做任何删除动作。
/// </summary>
public sealed class DuplicateFilesPageViewModel
{
    /// <summary>一组重复文件的展示条目：组内文件、当前保留份与待清理份。</summary>
    public sealed class DuplicateGroupItem
    {
        /// <summary>组标识（全文件 SHA-256，全组一致）。</summary>
        public string Sha256 { get; }

        /// <summary>单文件体积（组内全同，字节）。</summary>
        public long SizeBytes { get; }

        /// <summary>组内全部文件路径（与识别器输出同序）。</summary>
        public IReadOnlyList<string> FullPaths { get; }

        /// <summary>当前保留份路径（组内恒一份；改选经 TrySetKeep）。</summary>
        public string KeepPath { get; private set; }

        /// <summary>待清理份路径（保留份之外的全部，顺序与 FullPaths 一致）。</summary>
        public IReadOnlyList<string> PendingPaths { get; private set; }

        /// <summary>本组当前可释放体积（待清理份数 × 单文件体积）。</summary>
        public long ReclaimableBytes => SizeBytes * PendingPaths.Count;

        internal DuplicateGroupItem(DuplicateGroup group)
        {
            Sha256 = group.Sha256;
            SizeBytes = group.SizeBytes;
            FullPaths = group.Files.Select(f => f.FullPath).ToList();
            KeepPath = FullPaths[0]; // 默认保留组内第一份（确定性选择，用户可改选）
            PendingPaths = FullPaths.Skip(1).ToList();
        }

        /// <summary>
        /// 改选保留份：组内单选互斥——新保留份从待清理中移出，原保留份转为待清理。
        /// 路径不在本组时返回 false 且不改任何状态（界面提示无效选择，不抛异常打断）。
        /// </summary>
        public bool TrySetKeep(string fullPath)
        {
            if (!FullPaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase) || fullPath == KeepPath)
                return false;

            var pending = new List<string>(PendingPaths) { KeepPath };
            pending.Remove(fullPath);
            KeepPath = fullPath;
            PendingPaths = pending;
            return true;
        }
    }

    /// <summary>重复组清单（按当前可释放体积降序、同体积按组标识升序；构建后只读）。</summary>
    public IReadOnlyList<DuplicateGroupItem> Groups { get; private set; } = Array.Empty<DuplicateGroupItem>();

    /// <summary>概览文本（构建后刷新；空态如实「未发现重复文件」）。</summary>
    public string SummaryText { get; private set; } = "尚未扫描";

    /// <summary>是否有重复组（界面空态判断）。</summary>
    public bool HasDuplicates => Groups.Count > 0;

    /// <summary>全部待清理份数（各组待清理份合计）。</summary>
    public int PendingCount => Groups.Sum(g => g.PendingPaths.Count);

    /// <summary>按当前保留选择合计的可释放体积（字节）。</summary>
    public long TotalReclaimableBytes => Groups.Sum(g => g.ReclaimableBytes);

    /// <summary>
    /// 从重复文件识别结果建立展示态。文件数不足 2 的组跳过（无份可清理，防上游异常数据）；
    /// 入参 null 报红不静默。
    /// </summary>
    public DuplicateFilesPageViewModel BuildFromGroups(IReadOnlyList<DuplicateGroup>? groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        Groups = groups
            .Where(g => g.Files.Count >= 2)
            .Select(g => new DuplicateGroupItem(g))
            .OrderByDescending(g => g.ReclaimableBytes)
            .ThenBy(g => g.Sha256, StringComparer.Ordinal)
            .ToList();

        SummaryText = HasDuplicates
            ? $"发现 {Groups.Count} 组重复文件（共 {Groups.Sum(g => g.FullPaths.Count)} 个），保留各一份后可释放 {Fmt(TotalReclaimableBytes)}"
            : "未发现重复文件";
        return this;
    }

    /// <summary>
    /// 把当前全部待清理项转入统一清理确认队列（与工具箱/垃圾清理同一条确认链）：
    /// 路径级拒绝由闸门在源头标死，勾选与执行全部走 CleanupQueueViewModel / CleanupCoordinator。
    /// </summary>
    public CleanupQueueViewModel BuildCleanupQueue()
    {
        var items = Groups
            .SelectMany(g => g.PendingPaths, (g, path) => (path, g.SizeBytes))
            .Select(a => (a.path, a.SizeBytes, "duplicate", (string?)"duplicate-file"));
        return new CleanupQueueViewModel(items);
    }

    /// <summary>重复组条目主题色（重复文件=金，随主题即时取值）。</summary>
    public string GroupColorOf(ThemeTokens tokens) => tokens.Gold;

    private static string Fmt(long bytes)
    {
        double v = bytes;
        const long GB = 1024L * 1024 * 1024, MB = 1024 * 1024;
        return bytes >= GB ? $"{v / GB:F1} GB"
             : bytes >= MB ? $"{v / MB:F0} MB"
             : $"{bytes} B";
    }
}
