namespace TaoKeeper.AppKernel.Settings;

/// <summary>
/// R-P2-16 安装器勾选写入初始设置（纯映射，沙箱可测）。
/// 安装戳口径：MSI 恒写四值 0/1 到 HKCU\Software\Tao\TaoKeeper\InstallOptIns（未勾=0）；
/// 值不存在（null）=无安装器或旧版安装，保持代码默认值，不猜。
/// 首启（无设置文件）时套用一次，之后 AppSettingsStore 以设置文件为准，设置页可改。
/// </summary>
public static class InstallOptInDefaults
{
    /// <summary>安装戳快照：null=值不存在（不套用该维度），非 null=安装器决定（0/1）。</summary>
    public sealed record InstallerOptIns(bool? Autostart, bool? ContextMenu, bool? Widget, bool? WeeklyCleanup);

    /// <summary>把安装戳套到设置上（原地改并返回同一实例）。ContextMenu 暂无设置字段（R-P10-14 批落位），戳已存不丢。</summary>
    public static AppSettings Apply(AppSettings settings, InstallerOptIns? stamps)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (stamps is null) return settings;
        if (stamps.Autostart is { } autostart) settings.LaunchOnStartup = autostart;     // R-P2-16 与 R-P12-9 同源（单开关统一口径，2026-11-14）
        if (stamps.Widget is { } widget) settings.ShowDesktopWidget = widget;             // R-P11-4 不常驻口径
        if (stamps.WeeklyCleanup is { } weekly) settings.StorageSenseEnabled = weekly;    // R-P9-3 每周定时口径
        return settings;
    }
}
