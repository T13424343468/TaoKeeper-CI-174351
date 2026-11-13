using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TaoKeeper.AppKernel.Navigation;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>总览页快捷卡（标题/摘要/目标路由），跳转由壳侧按 AppRouteCatalog 执行。</summary>
public sealed record HomeQuickCard(string Title, string Subtitle, AppRoute Route);

/// <summary>清理账一览行（按发起批次聚合：同来源同分钟为一笔）。</summary>
public sealed record HomeLedgerLine(string TimeText, string SummaryText);

/// <summary>
/// 总览页视图模型：聚合体检评分、可清垃圾、可升级软件、内存占用与清理账最近几笔。
/// 道系铁律：未测/未扫/未知一律如实显示（不假数据、不假 100 分），
/// 副文与快捷卡摘要按数据有无切换「实测口径」与「尚无记录口径」。
/// 输入全部来自既有唯一事实源（CheckupPageViewModel / JunkScanResult / SoftwarePageViewModel /
/// MemoryMonitorEngine 读数 / CleanupLedger），本层只做转述与文案，不重算业务判定。
/// </summary>
public sealed class HomePageViewModel
{
    /// <summary>清理账一览行数上限（原型「清理账 · 最近三笔」）。</summary>
    public const int LedgerLineCount = 3;

    // ---- 体检（未测态：ScoreBandText=「未测」，评分不展示） ----
    public int? Score { get; private set; }
    public string ScoreBandText { get; private set; } = "未测";

    // ---- 可清垃圾（未扫态：null，界面按「尚未扫描」处理） ----
    public long? JunkBytes { get; private set; }
    public string JunkBytesText { get; private set; } = "尚未扫描";

    // ---- 软件可升级（未扫态：null） ----
    public int? UpgradeCount { get; private set; }
    public int BlockedCount { get; private set; }
    public string UpgradeSummaryText { get; private set; } = "尚未扫描软件清单";

    // ---- 内存占用（未知态：null，不显示百分比） ----
    public int? MemoryUsedPercent { get; private set; }

    /// <summary>上次体检时间（本地时区展示；未测为 null）。</summary>
    public DateTimeOffset? LastCheckupAt { get; private set; }

    // ---- 远山 hero 文案（随状态切换，不假数据） ----
    public string HeroTitle { get; private set; } = "身体尚可，余尘未清";
    public string HeroSubtitle { get; private set; } = "尚无体检记录，先做一次体检看看整体状况";

    public IReadOnlyList<HomeQuickCard> QuickCards { get; private set; } = Array.Empty<HomeQuickCard>();
    public IReadOnlyList<HomeLedgerLine> LedgerLines { get; private set; } = Array.Empty<HomeLedgerLine>();

    /// <summary>未测态构造：一切如实「尚未」，快捷卡照常给入口（供真机首启与体检前置使用）。</summary>
    public static HomePageViewModel Untested() =>
        Build(new CheckupPageViewModel().RunCheckup(HealthContext.Empty), null,
            null, null, null, null);

    /// <summary>实测态构造：体检页视图模型 + 上次体检时间 + 可选的垃圾/软件/内存读数与清理账（null=该项尚未扫描）。</summary>
    public static HomePageViewModel Build(
        CheckupPageViewModel checkup,
        DateTimeOffset? lastCheckupAtUtc,
        JunkScanResult? junk,
        SoftwarePageViewModel? software,
        int? memoryUsedPercent,
        IReadOnlyList<LedgerEntry>? ledgerEntries,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(checkup);
        return new HomePageViewModel().Apply(checkup, lastCheckupAtUtc, junk, software, memoryUsedPercent, ledgerEntries, now);
    }

    private HomePageViewModel Apply(
        CheckupPageViewModel checkup,
        DateTimeOffset? lastCheckupAtUtc,
        JunkScanResult? junk,
        SoftwarePageViewModel? software,
        int? memoryUsedPercent,
        IReadOnlyList<LedgerEntry>? ledgerEntries,
        DateTimeOffset? now)
    {
        var at = now ?? DateTimeOffset.Now;

        if (checkup.IsUntested)
        {
            Score = null;
            ScoreBandText = "未测";
            HeroTitle = "身体尚可，余尘未清";
            HeroSubtitle = "尚无体检记录，先做一次体检看看整体状况";
        }
        else
        {
            Score = checkup.Score;
            ScoreBandText = checkup.ScoreBandText;
            LastCheckupAt = lastCheckupAtUtc?.ToLocalTime();
            HeroTitle = checkup.Score >= 90 ? "身体尚可，余尘未清" : "有些要紧事，试试清一清";
            HeroSubtitle = BuildSubtitle(junk, software, at);
        }

        // 可清垃圾（任何体检态都如实展示；未扫=尚未扫描）
        if (junk is not null)
        {
            var bytes = junk.TotalBytes;
            JunkBytes = bytes;
            JunkBytesText = bytes <= 0 ? "很干净，暂无可清" : FormatBytes(bytes);
        }

        // 软件可升级 + 拦截（未扫=null；拦截数如实累加展示）
        if (software is not null)
        {
            UpgradeCount = software.SilentAllowed.Count + software.NeedConfirm.Count;
            BlockedCount = software.BlockedByPolicy.Count;
            UpgradeSummaryText = UpgradeCount.Value > 0
                ? $"{UpgradeCount.Value} 款软件可升级 · {BlockedCount} 项已拦截"
                : "软件都是最新的 · 无拦截";
        }

        MemoryUsedPercent = memoryUsedPercent is >= 0 and <= 100 ? memoryUsedPercent : null;

        QuickCards = BuildQuickCards();
        LedgerLines = BuildLedgerLines(ledgerEntries ?? Array.Empty<LedgerEntry>(), at);
        return this;
    }

    private string BuildSubtitle(JunkScanResult? junk, SoftwarePageViewModel? software, DateTimeOffset at)
    {
        // R-P1-3 定向回归修红：未体检时 LastCheckupAt 为空，强拆空值即「Nullable object must have a value」崩溃
        // （需求文档合并稿红灯②）；未测态副标题按 R-P1-3① 显示「时间未记」类未测文案，任何路径不抛异常。
        if (LastCheckupAt is null)
        {
            return "时间未记 · 跑一次体检见健康分";
        }
        var parts = new List<string>
        {
            $"上次体检 {FormatDay(LastCheckupAt.Value, at)} · 评分 {Score}",
        };
        if (junk is not null && junk.TotalBytes > 0) parts.Add($"{FormatBytes(junk.TotalBytes)} 垃圾待清");
        if (software is { HasUpgrades: true }) parts.Add($"{UpgradeCount} 款软件可升级");
        return string.Join(" · ", parts.Count > 1 ? parts : new List<string> { parts[0] + " · 状态良好" });
    }

    private IReadOnlyList<HomeQuickCard> BuildQuickCards() => new[]
    {
        new HomeQuickCard("软件管家", UpgradeSummaryText, AppRoute.SwStore),
        new HomeQuickCard("磁盘清理", JunkBytesText, AppRoute.Disk),
        new HomeQuickCard("内存管理", MemoryUsedPercent is int p ? $"物理内存 {p}%" : "内存占用未知", AppRoute.Mem),
        new HomeQuickCard("性能加速", "一键提速 · 后台收尾", AppRoute.Boost),
    };

    private static IReadOnlyList<HomeLedgerLine> BuildLedgerLines(
        IReadOnlyList<LedgerEntry> entries, DateTimeOffset now)
    {
        // 按发起批次聚合：同来源且相邻时间在 1 分钟内视为同一笔（账本无 run-id，最接近原型的「一笔」口径）
        var lines = new List<HomeLedgerLine>();
        if (entries.Count == 0) return lines;

        var groups = new List<(DateTimeOffset Head, string Source, int Count, long Bytes)>();
        foreach (var e in entries)
        {
            var at = e.AtUtc;
            if (groups.Count > 0)
            {
                var (head, source, count, bytes) = groups[^1];
                if (source == e.Source && (at - head).Duration() < TimeSpan.FromMinutes(1))
                {
                    groups[^1] = (head, source, count + 1, bytes + e.SizeBytes);
                    continue;
                }
            }
            groups.Add((at, e.Source, 1, e.SizeBytes));
        }

        // 原型口径：最新一笔在最上（时间倒序展示）
        foreach (var g in groups.TakeLast(LedgerLineCount).Reverse())
        {
            var local = g.Head.ToLocalTime();
            var time = $"{local.Month} 月 {local.Day} 日 {local:HH:mm}";
            var summary = g.Source switch
            {
                "junk" => "清理垃圾文件",
                "duplicates" => "清理重复文件",
                "storage-sense" => "存储感知自动清理",
                "widget" => "桌面挂件快捷清理",
                "manual" => "手动清理",
                _ => "清理",
            };
            lines.Add(new HomeLedgerLine(time,
                $"{time} {summary} {g.Count} 项，共 {FormatBytes(g.Bytes)}，已进回收站"));
        }
        return lines;
    }

    internal static string FormatDay(DateTimeOffset localValue, DateTimeOffset now)
    {
        var days = (now.Date - localValue.Date).TotalDays;
        return days switch
        {
            0 => "今天",
            1 => "昨天",
            < 30 => $"{localValue.Month} 月 {localValue.Day} 日",
            _ => $"{localValue.Year} 年 {localValue.Month} 月",
        };
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
        return bytes switch
        {
            >= GB => $"{(double)bytes / GB:0.#} GB",
            >= MB => $"{(double)bytes / MB:0.#} MB",
            >= KB => $"{(double)bytes / KB:0.#} KB",
            _ => $"{bytes} B",
        };
    }
}
