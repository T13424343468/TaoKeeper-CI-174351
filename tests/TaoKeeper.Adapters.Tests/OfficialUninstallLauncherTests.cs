using System;
using System.Threading.Tasks;
using TaoKeeper.Adapters;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Adapters.Tests;

/// <summary>
/// 官方卸载链测试（R-P2-5）：命令行拆解纯逻辑（引号路径/无引号空格路径/缺引号），
/// 条目路由（注册表投影走官方链、winget 条目拒绝降级由调用方处理）、执行结果映射。
/// 真实起进程归 Windows 真机（待真机项）。
/// </summary>
public class OfficialUninstallLauncherTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\App\uninstall.exe"" /silent", @"C:\Program Files\App\uninstall.exe", "/silent")]
    [InlineData(@"C:\PROGRA~1\App\uninst.exe", @"C:\PROGRA~1\App\uninst.exe", "")]
    [InlineData(@"C:\Program Files\App\uninst.exe /u /quiet", @"C:\Program Files\App\uninst.exe", "/u /quiet")]
    [InlineData(@"C:\app\setup.msi /x", @"C:\app\setup.msi", "/x")]
    public void 命令行拆解_引号优先_无引号按可执行扩展名试探(string line, string fileName, string arguments)
    {
        var (f, a) = OfficialUninstallLauncher.SplitCommand(line);
        Assert.Equal(fileName, f);
        Assert.Equal(arguments, a);
    }

    [Fact]
    public void 条目路由_盘符路径走官方链_winget包Id拒绝()
    {
        var registryEntry = new SoftwareEntry(@"C:\App\un.exe", "某软件", null, null, null, SoftwareSourceKind.OfficialPageManual);
        var wingetEntry = new SoftwareEntry("App.Publisher", "某软件", null, null, null, SoftwareSourceKind.WingetCommunity);

        Assert.True(OfficialUninstallLauncher.CanLaunch(registryEntry));
        Assert.False(OfficialUninstallLauncher.CanLaunch(wingetEntry));
        Assert.Throws<InvalidOperationException>(() =>
            new OfficialUninstallLauncher((f, a) => (0, string.Empty)).UninstallAsync(wingetEntry).Result);
    }

    [Fact]
    public async Task 执行_成功与失败如实映射_等待超时不强杀()
    {
        var ok = new OfficialUninstallLauncher((f, a) =>
        {
            Assert.Equal(@"C:\App\un.exe", f);
            Assert.Equal("/silent", a);
            return (0, string.Empty);
        });
        var result = await ok.UninstallAsync(new SoftwareEntry(
            @"C:\App\un.exe /silent", "某软件", null, null, null, SoftwareSourceKind.OfficialPageManual));
        Assert.True(result.Success);

        var fail = new OfficialUninstallLauncher((f, a) => (1602, "用户取消"));
        var failed = await fail.UninstallAsync(new SoftwareEntry(
            @"C:\App\un.exe", "某软件", null, null, null, SoftwareSourceKind.OfficialPageManual));
        Assert.False(failed.Success);
        Assert.Contains("1602", failed.Detail);
        Assert.Contains("用户取消", failed.Detail);
    }

    [Fact]
    public void 拆解_空串与孤引号如实拒绝不猜()
    {
        Assert.Equal((string.Empty, string.Empty), OfficialUninstallLauncher.SplitCommand(string.Empty));
        Assert.Equal((string.Empty, string.Empty), OfficialUninstallLauncher.SplitCommand(@"""未闭合"));
    }
}
