using System;
using System.Collections.Generic;
using TaoKeeper.AppKernel.Settings;
using TaoKeeper.AppKernel.Theming;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>设置页选项文案行（主题三选 / 挂件内容 / 挂件大小目录共用；IsSelected 随展示侧填写）。</summary>
public sealed record SettingsOptionText<T>(T Value, string DisplayText);

/// <summary>
/// 设置页视图模型：只读转述 AppSettings 既有模型 + 可写项走既有 AppSettingsStore 落盘契约
/// （不新造存储文件格式）。道系边界：默认零写动作——任何改动先进 Pending 副本，
/// 点「保存改动」经确认闸门后才写入设置文件；未接线存储时只在本会话内存生效并如实说明。
/// 非法输入（告警线越界、安装位置空值）一律防呆拒绝，不改待确认值、不假数据。
/// </summary>
public sealed class SettingsPageViewModel
{
    private readonly AppSettingsStore? _store;

    /// <summary>当前已确认生效的设置（无存储时为默认值）。</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>待确认副本：改动先进这里，确认闸门（保存）后才替换 Current。</summary>
    public AppSettings Pending { get; private set; } = new();

    /// <summary>是否已接设置存储契约（未接线时改动只在本会话内存生效）。</summary>
    public bool HasStore => _store is not null;

    /// <summary>是否有待确认改动（确认闸门前为 false，页面不出现保存条）。</summary>
    public bool HasPendingChanges => PendingChangeCount > 0;

    /// <summary>待确认改动条数（页面确认条计数用）。</summary>
    public int PendingChangeCount => BuildChangeSummaries(Current, Pending).Count;

    /// <summary>确认条文案：无改动 / 有改动 / 无存储渠道三种口径，如实转述。</summary>
    public string PendingSummaryText
    {
        get
        {
            if (!HasPendingChanges) return "暂无待确认改动。";
            return HasStore
                ? $"有 {PendingChangeCount} 项改动待确认：点「保存改动」写入设置文件，或「放弃」。"
                : $"有 {PendingChangeCount} 项改动待确认（暂无设置文件渠道，保存后仅本会话生效）。";
        }
    }

    /// <summary>保存按钮文案（无存储渠道时如实标明不落盘）。</summary>
    public string PendingSaveText =>
        HasStore ? "保存改动（写入设置文件）" : "保存改动（仅本会话生效）";

    // ── 构造 ──

    /// <summary>默认态构造：默认值展示，未接存储（改动仅本会话生效，确认条如实标明）。</summary>
    public SettingsPageViewModel() : this(store: null) { }

    /// <summary>接线构造：经既有 AppSettingsStore 读取（文件缺失/损坏由存储层回退默认值）。</summary>
    public SettingsPageViewModel(AppSettingsStore? store)
    {
        _store = store;
        Current = store is null ? new AppSettings() : store.Load();
        Pending = Current.Clone();
    }

    // ── 只读目录（文案映射，名称严格取自原型 v19 设置页） ──

    /// <summary>界面主题三选目录：水墨（默认）/ 天青 / 墨白（名称取自 ThemeTokens）。</summary>
    public static IReadOnlyList<SettingsOptionText<ThemeName>> ThemeCatalog { get; } = new[]
    {
        new SettingsOptionText<ThemeName>(ThemeName.Shuimo, ThemeTokens.Of(ThemeName.Shuimo).DisplayName),
        new SettingsOptionText<ThemeName>(ThemeName.Tianqing, ThemeTokens.Of(ThemeName.Tianqing).DisplayName),
        new SettingsOptionText<ThemeName>(ThemeName.Mobai, ThemeTokens.Of(ThemeName.Mobai).DisplayName),
    };

    /// <summary>挂件显示内容目录：内存 / 垃圾待清 / 磁盘余量。</summary>
    public static IReadOnlyList<SettingsOptionText<WidgetContent>> WidgetContentCatalog { get; } = new[]
    {
        new SettingsOptionText<WidgetContent>(WidgetContent.Memory, "内存"),
        new SettingsOptionText<WidgetContent>(WidgetContent.JunkPending, "垃圾待清"),
        new SettingsOptionText<WidgetContent>(WidgetContent.DiskFree, "磁盘余量"),
    };

    /// <summary>挂件大小两档目录：标准 / 紧凑。</summary>
    public static IReadOnlyList<SettingsOptionText<WidgetSize>> WidgetSizeCatalog { get; } = new[]
    {
        new SettingsOptionText<WidgetSize>(WidgetSize.Standard, "标准"),
        new SettingsOptionText<WidgetSize>(WidgetSize.Compact, "紧凑"),
    };

    /// <summary>红线锁定 pill 文案（模型层常量，任何写入路径不可绕过）。</summary>
    public IReadOnlyList<string> LockedPills => AppSettingsGuard.LockedPills;

    /// <summary>内存告警线行文案：默认 85% 可调可关；已按待确认值如实展示（0=不提醒）。</summary>
    public string MemoryAlertLine => Pending.MemoryAlertPercent switch
    {
        0 => "已关闭（0=不提醒）",
        var p => $"物理内存超过 {p}% 才提醒 · 默认 85%，可调可关",
    };

    /// <summary>磁盘余量提醒行文案：默认 10 GB 可调；已按待确认值如实展示（0=不提醒）。</summary>
    public string DiskFreeAlertLine => Pending.DiskFreeAlertGb switch
    {
        0 => "已关闭（0=不提醒）",
        var g => $"C 盘剩余不足 {FormatGb(g)} GB 时提醒 · 默认 10 GB，可调",
    };

    /// <summary>默认安装位置行文案（每款软件装前可单独改）。</summary>
    public string DefaultInstallDirLine => Pending.DefaultInstallDir;

    private static string FormatGb(double gb) => gb == Math.Floor(gb) ? $"{gb:0}" : $"{gb:0.#}";

    private static string ThemeText(ThemeName theme) => ThemeTokens.Of(theme).DisplayName;

    private static string ContentText(WidgetContent content) => TextOf(WidgetContentCatalog, content);

    private static string SizeText(WidgetSize size) => TextOf(WidgetSizeCatalog, size);

    private static string TextOf<T>(IReadOnlyList<SettingsOptionText<T>> catalog, T value)
        where T : notnull
    {
        foreach (var option in catalog)
            if (EqualityComparer<T>.Default.Equals(option.Value, value)) return option.DisplayText;
        return value.ToString() ?? string.Empty;
    }

    private static string OnOff(bool on) => on ? "开" : "关";

    // ── 可写项：改动一律进 Pending（确认闸门前不动 Current、不落盘） ──

    public void SetPendingTheme(ThemeName theme) => Pending.Theme = theme;

    public void SetPendingLaunchOnStartup(bool on) => Pending.LaunchOnStartup = on;

    public void SetPendingAutoCheckupOnStartup(bool on) => Pending.AutoCheckupOnStartup = on;

    public void SetPendingSilentUpgrade(bool on) => Pending.SilentUpgrade = on;

    public void SetPendingCreateShortcutAfterInstall(bool on) => Pending.CreateShortcutAfterInstall = on;

    public void SetPendingRecordInstallLedger(bool on) => Pending.RecordInstallLedger = on;

    public void SetPendingShowDesktopWidget(bool on) => Pending.ShowDesktopWidget = on;

    public void SetPendingStorageSenseEnabled(bool on) => Pending.StorageSenseEnabled = on;

    public void SetPendingQuickScanByDefault(bool on) => Pending.QuickScanByDefault = on;

    public void SetPendingWidgetSize(WidgetSize size) => Pending.WidgetSize = size;

    /// <summary>挂件显示内容勾选：原型「想看哪样勾哪样」，空集允许（挂件可只留壳）。</summary>
    public void TogglePendingWidgetContent(WidgetContent content)
    {
        if (!Pending.WidgetContents.Remove(content)) Pending.WidgetContents.Add(content);
    }

    /// <summary>内存告警线防呆：只接受 0–100（0=不提醒）；越界拒绝并给文案，待确认值不动。</summary>
    public bool TrySetPendingMemoryAlertPercent(int percent, out string error)
    {
        if (percent is < 0 or > 100)
        {
            error = "内存告警线需在 0–100% 之间（0=不提醒），未记入待确认值。";
            return false;
        }
        Pending.MemoryAlertPercent = percent;
        error = string.Empty;
        return true;
    }

    /// <summary>磁盘余量提醒防呆：只接受 0–1024 GB（0=不提醒）；越界/非数值拒绝并给文案。</summary>
    public bool TrySetPendingDiskFreeAlertGb(double gb, out string error)
    {
        if (double.IsNaN(gb) || gb < 0 || gb > 1024)
        {
            error = "磁盘余量提醒需在 0–1024 GB 之间（0=不提醒），未记入待确认值。";
            return false;
        }
        Pending.DiskFreeAlertGb = gb;
        error = string.Empty;
        return true;
    }

    /// <summary>默认安装位置防呆：不允许空值；空值拒绝并给文案，待确认值不动。</summary>
    public bool TrySetPendingDefaultInstallDir(string? dir, out string error)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            error = "安装位置不能为空，未记入待确认值。";
            return false;
        }
        Pending.DefaultInstallDir = dir!.Trim();
        error = string.Empty;
        return true;
    }

    // ── 确认闸门 ──

    /// <summary>
    /// 确认闸门：把待确认改动落为当前设置；已接存储时经既有 AppSettingsStore.Save 原子写入。
    /// 无改动时返回空清单且不触碰存储；返回值为本次生效的改动摘要（「旧值 → 新值」）。
    /// </summary>
    public IReadOnlyList<string> ConfirmPendingChanges()
    {
        var summaries = BuildChangeSummaries(Current, Pending);
        if (summaries.Count == 0) return summaries;

        Current = Pending.Clone();
        _store?.Save(Current);
        Pending = Current.Clone();
        return summaries;
    }

    /// <summary>放弃待确认改动：待确认副本回到当前设置，文件零触碰。</summary>
    public void DiscardPendingChanges() => Pending = Current.Clone();

    // ── 改动摘要（确认条与保存回执共用） ──

    private static List<string> BuildChangeSummaries(AppSettings before, AppSettings after)
    {
        var list = new List<string>();

        if (before.Theme != after.Theme)
            list.Add($"界面主题：{ThemeText(before.Theme)} → {ThemeText(after.Theme)}");
        if (before.LaunchOnStartup != after.LaunchOnStartup)
            list.Add($"开机自动启动：{OnOff(before.LaunchOnStartup)} → {OnOff(after.LaunchOnStartup)}");
        if (before.AutoCheckupOnStartup != after.AutoCheckupOnStartup)
            list.Add($"开机后自动体检：{OnOff(before.AutoCheckupOnStartup)} → {OnOff(after.AutoCheckupOnStartup)}");
        if (before.DefaultInstallDir != after.DefaultInstallDir)
            list.Add($"默认安装位置：{before.DefaultInstallDir} → {after.DefaultInstallDir}");
        if (before.SilentUpgrade != after.SilentUpgrade)
            list.Add($"静默升级：{OnOff(before.SilentUpgrade)} → {OnOff(after.SilentUpgrade)}");
        if (before.CreateShortcutAfterInstall != after.CreateShortcutAfterInstall)
            list.Add($"装完自动建桌面快捷方式：{OnOff(before.CreateShortcutAfterInstall)} → {OnOff(after.CreateShortcutAfterInstall)}");
        if (before.RecordInstallLedger != after.RecordInstallLedger)
            list.Add($"装完记入安装账：{OnOff(before.RecordInstallLedger)} → {OnOff(after.RecordInstallLedger)}");
        if (before.ShowDesktopWidget != after.ShowDesktopWidget)
            list.Add($"显示桌面挂件：{OnOff(before.ShowDesktopWidget)} → {OnOff(after.ShowDesktopWidget)}");
        if (before.MemoryAlertPercent != after.MemoryAlertPercent)
            list.Add($"内存告警线：{before.MemoryAlertPercent}% → {after.MemoryAlertPercent}%");
        if (before.DiskFreeAlertGb != after.DiskFreeAlertGb)
            list.Add($"磁盘余量提醒：{FormatGb(before.DiskFreeAlertGb)} GB → {FormatGb(after.DiskFreeAlertGb)} GB");
        if (before.StorageSenseEnabled != after.StorageSenseEnabled)
            list.Add($"存储感知定时清理：{OnOff(before.StorageSenseEnabled)} → {OnOff(after.StorageSenseEnabled)}");
        if (before.QuickScanByDefault != after.QuickScanByDefault)
            list.Add($"快扫默认开启：{OnOff(before.QuickScanByDefault)} → {OnOff(after.QuickScanByDefault)}");
        if (before.WidgetSize != after.WidgetSize)
            list.Add($"挂件大小：{SizeText(before.WidgetSize)} → {SizeText(after.WidgetSize)}");

        foreach (var content in WidgetContentCatalog)
        {
            var was = before.WidgetContents.Contains(content.Value);
            var now = after.WidgetContents.Contains(content.Value);
            if (was != now) list.Add($"挂件显示内容 · {content.DisplayText}：{OnOff(was)} → {OnOff(now)}");
        }

        return list;
    }
}
