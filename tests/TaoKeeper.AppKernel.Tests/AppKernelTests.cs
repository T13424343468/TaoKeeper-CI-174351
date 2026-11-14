using TaoKeeper.AppKernel.Navigation;
using TaoKeeper.AppKernel.Settings;
using TaoKeeper.AppKernel.Theming;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

public class NavRouteTests
{
    [Fact]
    public void 一级导航与需求稿G6一致恒7项()
    {
        var expected = new[] { "home", "checkup", "disk", "swstore", "boost", "tools", "settings" };
        Assert.Equal(expected, AppRouteCatalog.NavBarRoutes.Select(r => AppRouteCatalog.Items.First(i => i.Route == r).PageId).ToArray());
        Assert.Equal(7, AppRouteCatalog.NavBarRoutes.Length);
    }

    [Fact]
    public void 二级收纳页不入侧栏导航条()
    {
        // G6：工具箱二级收纳——内存/驱动/弹窗/存储感知/桌面挂件经工具箱等入口进入，设计说明开发期隐藏
        var secondary = new[] { AppRoute.Mem, AppRoute.Driver, AppRoute.Popup, AppRoute.Storage, AppRoute.Widget, AppRoute.Design };
        foreach (var r in secondary)
            Assert.DoesNotContain(r, AppRouteCatalog.NavBarRoutes);
    }

    [Fact]
    public void 页面中文名与需求稿一致()
    {
        var expected = new[]
        {
            "总览", "系统体检", "磁盘清理", "软件管家", "性能加速", "工具箱", "设置",
            "内存管理", "驱动安装升级", "弹窗管理", "存储感知", "桌面挂件", "设计说明",
        };
        Assert.Equal(expected, AppRouteCatalog.Items.Select(i => i.Title).ToArray());
    }
}

public class ThemeTokensTests
{
    [Theory]
    [InlineData("shuimo", "水墨")]
    [InlineData("tianqing", "天青")]
    [InlineData("mobai", "墨白")]
    public void 三主题名相与原型一致(string id, string name)
    {
        var t = ThemeTokens.Of(id switch
        {
            "shuimo" => ThemeName.Shuimo,
            "tianqing" => ThemeName.Tianqing,
            _ => ThemeName.Mobai,
        });
        Assert.Equal(id, t.Id);
        Assert.Equal(name, t.DisplayName);
    }

    [Fact]
    public void 主题令牌值与原型v19CSS逐字一致()
    {
        // 水墨（:root）
        var s = ThemeTokens.Shuimo;
        Assert.Equal("#f6f5f1", s.Paper);
        Assert.Equal("rgba(255,255,255,.74)", s.Card);
        Assert.Equal("#b5443a", s.Cinnabar);
        Assert.Equal("rgba(255,255,255,.95)", s.Glaze);
        Assert.Equal("#b5443a", s.HeroSun);
        Assert.Null(s.BodyBackground);

        // 天青覆盖段
        var q = ThemeTokens.Tianqing;
        Assert.Equal("#f0f7fb", q.Paper);
        Assert.Equal("#e2b45c", q.HeroSun);
        Assert.Equal(s.Cinnabar, q.Cinnabar); // 未覆盖项继承 :root

        // 墨白覆盖段
        var m = ThemeTokens.Mobai;
        Assert.Equal("#191b1c", m.Paper);
        Assert.Equal("#0d0e0f", m.BodyBackground);
        Assert.Equal("#4e7d8c", m.Strong);
        Assert.Equal("rgba(255,255,255,.22)", m.Glaze);
    }

    [Fact]
    public void 默认主题为水墨()
    {
        Assert.Equal(ThemeName.Shuimo, new AppSettings().Theme);
        Assert.Equal("shuimo", ThemeTokens.Default.Id);
    }

    [Fact]
    public void 所有主题必备令牌齐全()
    {
        foreach (var t in new[] { ThemeTokens.Shuimo, ThemeTokens.Tianqing, ThemeTokens.Mobai })
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Paper));
            Assert.False(string.IsNullOrWhiteSpace(t.Ink));
            Assert.False(string.IsNullOrWhiteSpace(t.Azure));
            Assert.False(string.IsNullOrWhiteSpace(t.Glaze));
            Assert.False(string.IsNullOrWhiteSpace(t.GlazeSoft));
            Assert.False(string.IsNullOrWhiteSpace(t.GlazeTop));
            Assert.False(string.IsNullOrWhiteSpace(t.HeroSky));
            Assert.False(string.IsNullOrWhiteSpace(t.Radius));
        }
    }
}

public class AppSettingsTests
{
    [Fact]
    public void 默认值与原型v19开关初始态逐项一致()
    {
        var s = new AppSettings();
        Assert.True(s.LaunchOnStartup);          // 开机自动启动：on
        Assert.True(s.AutoCheckupOnStartup);     // 开机后自动体检：on
        Assert.Equal(@"D:\TaoKeeper\Soft", s.DefaultInstallDir); // 默认安装位置
        Assert.False(s.SilentUpgrade);           // 静默升级：默认关
        Assert.True(s.CreateShortcutAfterInstall); // 装完自动建桌面快捷方式：默认开
        Assert.True(s.RecordInstallLedger);      // 装完记入安装账：默认开
        Assert.False(s.StorageSenseEnabled);     // 存储感知定时清理：默认关
        Assert.True(s.QuickScanByDefault);       // 快扫默认开启
        Assert.Equal(85, s.MemoryAlertPercent);  // 内存告警线：默认 85%
        Assert.Equal(10.0, s.DiskFreeAlertGb);   // 磁盘余量提醒：默认 10 GB
        Assert.True(s.ShowDesktopWidget);        // 显示桌面挂件：on
        Assert.Equal(WidgetSize.Standard, s.WidgetSize); // 挂件大小：标准
        // 显示内容：内存 on、垃圾待清 on、磁盘余量 off
        Assert.Contains(WidgetContent.Memory, s.WidgetContents);
        Assert.Contains(WidgetContent.JunkPending, s.WidgetContents);
        Assert.DoesNotContain(WidgetContent.DiskFree, s.WidgetContents);
    }

    [Fact]
    public void 红线锁定项只以文案存在()
    {
        Assert.Equal(3, AppSettingsGuard.LockedPills.Length);
        Assert.Contains(AppSettingsGuard.OfficialSourceLockedMessage, AppSettingsGuard.LockedPills);
        Assert.Contains(AppSettingsGuard.HashCheckLockedMessage, AppSettingsGuard.LockedPills);
        Assert.Contains(AppSettingsGuard.RecycleBinLockedMessage, AppSettingsGuard.LockedPills);
        // 红线不作为设置字段存在：模型无对应开关属性
        Assert.Equal(0, typeof(AppSettings).GetProperties().Count(p => p.Name.Contains("Locked") || p.Name.Contains("HashCheck") || p.Name.Contains("OfficialSource") || p.Name.Contains("Recycle")));
    }

    [Fact]
    public void 阈值可调且边界合法()
    {
        var s = new AppSettings();
        s.MemoryAlertPercent = 0;      // 可关
        s.MemoryAlertPercent = 95;     // 可调
        s.DiskFreeAlertGb = 5;
        Assert.Equal(95, s.MemoryAlertPercent);
        Assert.Equal(5, s.DiskFreeAlertGb);
    }

    [Fact]
    public void Clone深拷贝挂件集合()
    {
        var s = new AppSettings();
        var c = s.Clone();
        c.WidgetContents.Remove(WidgetContent.Memory);
        Assert.Contains(WidgetContent.Memory, s.WidgetContents); // 原件不受影响
        c.MemoryAlertPercent = 99;
        Assert.Equal(85, s.MemoryAlertPercent);
    }
}

public class AppSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taokeeper-tests", Guid.NewGuid().ToString("N"));
    private readonly AppSettingsStore _store;

    public AppSettingsStoreTests() => _store = new AppSettingsStore(Path.Combine(_dir, "settings.json"));

    [Fact]
    public void 无文件时回默认值()
    {
        var s = _store.Load();
        Assert.Equal(ThemeName.Shuimo, s.Theme);
        Assert.Equal(85, s.MemoryAlertPercent);
    }

    [Fact]
    public void 存取往返且序列化不含红线开关()
    {
        var s = new AppSettings { Theme = ThemeName.Mobai, MemoryAlertPercent = 90 };
        _store.Save(s);
        var json = File.ReadAllText(_store.FilePath);
        Assert.Contains("\"Theme\"", json);
        Assert.DoesNotContain("HashCheck", json);
        Assert.DoesNotContain("OfficialSource", json);
        Assert.DoesNotContain("Recycle", json);

        var back = _store.Load();
        Assert.Equal(ThemeName.Mobai, back.Theme);
        Assert.Equal(90, back.MemoryAlertPercent);
    }

    [Fact]
    public void 损坏文件回退默认值并留副本()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.FilePath, "{ not valid json !!");
        var s = _store.Load();
        Assert.Equal(ThemeName.Shuimo, s.Theme); // 默认值
        Assert.True(File.Exists(_store.FilePath + ".corrupt"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
