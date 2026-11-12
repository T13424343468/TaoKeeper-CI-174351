using TaoKeeper.AppKernel.Navigation;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

public class LaunchRouteTests
{
    [Fact]
    public void 未传参返回空_保持首页()
    {
        Assert.Null(LaunchRoute.FromCommandLine(Array.Empty<string>()));
        Assert.Null(LaunchRoute.FromCommandLine(new[] { "TaoKeeper.App.exe" }));
        Assert.Null(LaunchRoute.FromCommandLine(new[] { "--page" })); // 缺值不解析
    }

    [Fact]
    public void 按页面ID直达_大小写不敏感()
    {
        Assert.Equal(AppRoute.Disk, LaunchRoute.FromCommandLine(new[] { "--page", "disk" }));
        Assert.Equal(AppRoute.Disk, LaunchRoute.FromCommandLine(new[] { "--page", "Disk" }));
        Assert.Equal(AppRoute.Settings, LaunchRoute.FromCommandLine(new[] { "--page", "settings" }));
    }

    [Fact]
    public void 按中文标题直达()
    {
        Assert.Equal(AppRoute.Disk, LaunchRoute.FromCommandLine(new[] { "--page", "磁盘清理" }));
        Assert.Equal(AppRoute.Home, LaunchRoute.FromCommandLine(new[] { "--page", "总览" }));
    }

    [Fact]
    public void 未识别路由返回空_不新增路由()
    {
        Assert.Null(LaunchRoute.FromCommandLine(new[] { "--page", "不存在" }));
        Assert.Null(LaunchRoute.FromCommandLine(new[] { "--page", "" }));
        Assert.Null(LaunchRoute.FromCommandLine(new[] { "--page", "  " })); // 纯空白不解析
    }

    [Fact]
    public void 取最后一个page参数生效()
    {
        Assert.Equal(
            AppRoute.Settings,
            LaunchRoute.FromCommandLine(new[] { "--page", "disk", "--page", "settings" }));
    }

    [Fact]
    public void 其他参数不干扰解析()
    {
        Assert.Equal(
            AppRoute.Disk,
            LaunchRoute.FromCommandLine(new[] { "--theme", "ink", "--page", "disk", "--verbose" }));
    }
}
