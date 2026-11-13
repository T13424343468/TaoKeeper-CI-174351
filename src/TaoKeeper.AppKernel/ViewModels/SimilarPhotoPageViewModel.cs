using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>
/// 相似照片页视图模型（UI 框架零依赖、零写动作）：
/// 接 SimilarPhotoFinder 的感知哈希分组，逐组展示、逐张勾选（R-P10-2b），
/// 处理动作转统一清理确认队列（R-P10-2c，走 0.2 分层语义：用户照片默认回收站、可勾直接删除）。
/// 本模型不做任何删除动作，删除编排全部复用既有确认闸门与执行链。
/// </summary>
public sealed class SimilarPhotoPageViewModel
{
    /// <summary>一组相似照片的展示条目：组内逐张，勾选=拟处理（默认保留建议份不勾）。</summary>
    public sealed class SimilarGroupItem
    {
        /// <summary>组内全部照片（已按分辨率降序排列，建议保留份恒在首位）。</summary>
        public IReadOnlyList<PhotoFingerprint> Photos { get; }

        /// <summary>建议保留份路径（组内分辨率最高者；改选经 SetKeep）。</summary>
        public string KeepPath { get; private set; }

        /// <summary>拟处理勾选位（路径 → 是否勾选；保留份恒为 false）。</summary>
        private readonly Dictionary<string, bool> _selected = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>本组拟处理张数。</summary>
        public int SelectedCount => _selected.Count(kv => kv.Value);

        /// <summary>本组拟处理合计字节。</summary>
        public long SelectedBytes => Photos.Where(p => IsSelected(p.FullPath)).Sum(p => p.SizeBytes);

        /// <summary>本组占用合计字节（全部照片）。</summary>
        public long TotalBytes => Photos.Sum(p => p.SizeBytes);

        internal SimilarGroupItem(IReadOnlyList<PhotoFingerprint> photos)
        {
            // 展示层自排序（像素数降序），不信任上游已排——建议保留=组内分辨率最高，口径在本层收口
            Photos = photos.OrderByDescending(p => p.PixelCount).ToArray();
            KeepPath = Photos[0].FullPath;
            foreach (var p in photos)
                _selected[p.FullPath] = p.FullPath != KeepPath;
        }

        /// <summary>某张是否勾选拟处理。</summary>
        public bool IsSelected(string fullPath) =>
            _selected.TryGetValue(fullPath, out var on) && on;

        /// <summary>
        /// 翻转某张的勾选位；保留份不可勾（返回 false 且不改状态）。
        /// 保留数不限——保留=未勾选，想多留几张就多取消几张。
        /// </summary>
        public bool Toggle(string fullPath)
        {
            if (fullPath == KeepPath || !_selected.ContainsKey(fullPath))
                return false;
            _selected[fullPath] = !_selected[fullPath];
            return true;
        }

        /// <summary>
        /// 改选保留份：新保留份取消勾选，原保留份转为可勾并默认勾上（组内恒有建议位）。
        /// 路径不在本组时返回 false 且不改任何状态。
        /// </summary>
        public bool SetKeep(string fullPath)
        {
            if (fullPath == KeepPath || !_selected.ContainsKey(fullPath))
                return false;
            _selected[KeepPath] = true;
            _selected[fullPath] = false;
            KeepPath = fullPath;
            return true;
        }
    }

    /// <summary>相似组清单（识别器输出序，构建后只读）。</summary>
    public IReadOnlyList<SimilarGroupItem> Groups { get; private set; } = Array.Empty<SimilarGroupItem>();

    /// <summary>概览文本（空态=「没找到相似照片」，非白板、非占位）。</summary>
    public string SummaryText { get; private set; } = "尚未扫描";

    /// <summary>是否有相似组（界面空态判断）。</summary>
    public bool HasGroups => Groups.Count > 0;

    /// <summary>全部拟处理张数。</summary>
    public int SelectedCount => Groups.Sum(g => g.SelectedCount);

    /// <summary>按当前勾选合计的处理空间（字节）。</summary>
    public long SelectedBytes => Groups.Sum(g => g.SelectedBytes);

    /// <summary>
    /// 从识别结果建立展示态。入参 null 报红不静默；空清单如实落「没找到相似照片」。
    /// </summary>
    public SimilarPhotoPageViewModel BuildFromGroups(IReadOnlyList<SimilarPhotoGroup>? groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        Groups = groups
            .Where(g => g.Photos.Count >= 2)
            .Select(g => new SimilarGroupItem(g.Photos))
            .ToList();

        SummaryText = HasGroups
            ? $"发现 {Groups.Count} 组相似照片（共 {Groups.Sum(g => g.Photos.Count)} 张），按建议保留各一张后可处理 {SelectedCount} 张，释放 {Fmt(SelectedBytes)}"
            : "没找到相似照片";
        return this;
    }

    /// <summary>
    /// 把当前勾选的拟处理项转入统一清理确认队列（与磁盘清理/重复文件同一条确认链）。
    /// 空勾选返回空队列（界面提示无可处理项，不弹确认）。
    /// </summary>
    public CleanupQueueViewModel BuildCleanupQueue()
    {
        var items = Groups
            .SelectMany(g => g.Photos, (g, p) => (p, g.IsSelected(p.FullPath)))
            .Where(a => a.Item2)
            .Select(a => (a.p.FullPath, a.p.SizeBytes, "similar-photo", (string?)"similar-photo"));
        var queue = new CleanupQueueViewModel(items);
        queue.SelectAll(); // 拟处理即用户选定的清理意图；受保护项勾不上（闸门源头标死），BuildActions 只含可执行项
        return queue;
    }

    private static string Fmt(long bytes)
    {
        double v = bytes;
        const long GB = 1024L * 1024 * 1024, MB = 1024 * 1024;
        return bytes >= GB ? $"{v / GB:F1} GB"
             : bytes >= MB ? $"{v / MB:F0} MB"
             : $"{bytes} B";
    }
}
