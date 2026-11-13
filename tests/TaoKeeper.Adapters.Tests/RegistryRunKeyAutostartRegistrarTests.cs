using TaoKeeper.Adapters;
using Xunit;

namespace TaoKeeper.Adapters.Tests;

/// <summary>托盘开机自启注册器（R-P12-9）：值命令行构造纯函数 + 沙箱（非 Windows）诚实降级不假成功。</summary>
public class RegistryRunKeyAutostartRegistrarTests
{
    [Fact]
    public void Run键命令行_路径含空格整体加引号()
    {
        Assert.Equal("\"C:\\Program Files\\TaoKeeper\\TaoKeeper.exe\"",
            RegistryRunKeyAutostartRegistrar.RunCommandFor(@"C:\Program Files\TaoKeeper\TaoKeeper.exe"));
        Assert.Equal("\"D:\\TaoKeeper\\TaoKeeper.exe\"", RegistryRunKeyAutostartRegistrar.RunCommandFor(@"D:\TaoKeeper\TaoKeeper.exe"));
    }

    [Fact]
    public void 沙箱非Windows_注册动作如实失败_不假成功()
    {
        // 沙箱跑在 Linux：注册表不可用——按道系边界必须如实失败并给可读原因，不静默假成功
        var registrar = new RegistryRunKeyAutostartRegistrar();

        Assert.False(registrar.IsRegistered());
        Assert.False(registrar.TryRegister(out var registerError));
        Assert.Contains("Windows", registerError);
        Assert.False(registrar.TryUnregister(out var unregisterError));
        Assert.Contains("Windows", unregisterError);
    }

    [Fact]
    public void 值名常量_与WiX打包清理口径一致()
    {
        // 卸载清理按此值名删键；改名须 Product.wxs 与本实现三处同步
        Assert.Equal("TaoKeeper", RegistryRunKeyAutostartRegistrar.ValueName);
    }
}
