using TaoKeeper.AppKernel.Settings;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>R-P2-16 安装器勾选写入初始设置：纯映射断言（默认值溯源 V-A10：戳为事实源，null=不套用）。</summary>
public class InstallOptInDefaultsTests
{
    private static InstallOptInDefaults.InstallerOptIns Stamps(
        bool? autostart = null, bool? contextMenu = null, bool? widget = null, bool? weekly = null) =>
        new(autostart, contextMenu, widget, weekly);

    [Fact]
    public void 无戳_保持代码默认值不猜()
    {
        var before = new AppSettings().Clone();

        var after = InstallOptInDefaults.Apply(new AppSettings(), null);
        var afterNoStamps = InstallOptInDefaults.Apply(new AppSettings(), Stamps());

        Assert.Equal(before.LaunchOnStartup, after.LaunchOnStartup);
        Assert.Equal(before.ShowDesktopWidget, after.ShowDesktopWidget);
        Assert.Equal(before.StorageSenseEnabled, after.StorageSenseEnabled);
        Assert.Equal(before.LaunchOnStartup, afterNoStamps.LaunchOnStartup);
        Assert.Equal(before.ShowDesktopWidget, afterNoStamps.ShowDesktopWidget);
        Assert.Equal(before.StorageSenseEnabled, afterNoStamps.StorageSenseEnabled);
    }

    [Fact]
    public void 全勾_对应设置逐项转正()
    {
        var settings = InstallOptInDefaults.Apply(new AppSettings(), Stamps(autostart: true, contextMenu: true, widget: true, weekly: true));

        Assert.True(settings.LaunchOnStartup);      // R-P12-9 同源口径（单开关统一，2026-11-14）
        Assert.True(settings.ShowDesktopWidget);    // R-P11-4 口径
        Assert.True(settings.StorageSenseEnabled);  // R-P9-3 每周定时口径
    }

    [Fact]
    public void 全不勾_对应设置逐项落零()
    {
        var settings = InstallOptInDefaults.Apply(new AppSettings(), Stamps(autostart: false, contextMenu: false, widget: false, weekly: false));

        Assert.False(settings.LaunchOnStartup);
        Assert.False(settings.ShowDesktopWidget);
        Assert.False(settings.StorageSenseEnabled);
    }

    [Fact]
    public void 部分戳_未戳字段保持代码默认不误伤()
    {
        var settings = InstallOptInDefaults.Apply(new AppSettings(), Stamps(weekly: true));

        // 代码默认 LaunchOnStartup=true / ShowDesktopWidget=true：无戳维度原样保留
        Assert.True(settings.LaunchOnStartup);
        Assert.True(settings.ShowDesktopWidget);
        Assert.True(settings.StorageSenseEnabled);
    }

    [Fact]
    public void 设置页已改值_映射仍可执行但不覆盖既有文件口径由调用方保证()
    {
        // 语义护栏：映射只按戳改三个已知字段，不碰其余设置（红线项本就不在模型内）
        var settings = new AppSettings { Theme = TaoKeeper.AppKernel.Theming.ThemeName.Shuimo };
        InstallOptInDefaults.Apply(settings, Stamps(widget: false));

        Assert.Equal(TaoKeeper.AppKernel.Theming.ThemeName.Shuimo, settings.Theme);
    }
}
