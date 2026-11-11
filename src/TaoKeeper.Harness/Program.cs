using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TaoKeeper.Adapters;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Harness;

/// <summary>
/// TaoKeeper 真机自检台（只读，零写动作）：
/// 在真机上验证 winget 软件源适配器与驱动适配层的实测行为，输出实测证据。
/// 用法：dotnet run --project src/TaoKeeper.Harness -- [list-software|list-drivers|driver-updates|all]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 重定向管道时可能不支持，忽略 */ }

        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        Console.WriteLine("TaoKeeper 真机自检台 v0.1（只读自检，零写动作，不做任何安装/升级/删除）");
        Console.WriteLine(new string('=', 72));

        var fail = 0;
        if (mode is "all" or "list-software")
            fail += RunCheck("软件管家·winget 清单", CheckWinget);
        if (mode is "all" or "list-drivers")
            fail += RunCheck("驱动管家·pnputil 驱动清册", CheckPnputil);
        if (mode is "all" or "driver-updates")
            fail += RunCheck("驱动管家·WUA 驱动更新搜索", CheckWua);

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "自检完成：全部通道通过。请把本输出全文（或截图）回传给大光做真机回流验证。"
            : $"自检完成：{fail} 个通道报红。请把本输出全文（或截图）回传给大光，按报红内容修复。");
        return fail == 0 ? 0 : 2;
    }

    private static int RunCheck(string title, Func<Task<string>> check)
    {
        Console.WriteLine();
        Console.WriteLine($"[{title}]");
        try
        {
            var report = check().GetAwaiter().GetResult();
            Console.WriteLine(report);
            return 0;
        }
        catch (Exception ex)
        {
            var reason = ex is PlatformNotSupportedException or InvalidOperationException or FormatException
                ? ex.Message
                : "未预期的异常：" + ex.GetType().Name + "（请整段回传给大光，不要自行修改系统）";
            Console.WriteLine("  报红：" + reason);
            return 1;
        }
    }

    private static Task<string> CheckWinget() => Task.Run(() =>
    {
        var adapter = new WingetSoftwareSourceAdapter();
        var installed = adapter.ListInstalledAsync().GetAwaiter().GetResult();
        var upgrades = adapter.ListUpgradesAsync().GetAwaiter().GetResult();
        var head = installed.Take(8).ToList();
        var lines = new StringBuilder();
        lines.AppendLine($"  winget 可用，已安装 {installed.Count} 个软件，可升级 {upgrades.Count} 个。");
        lines.AppendLine("  已安装样例（前 8 条）：");
        foreach (var s in head)
            lines.AppendLine($"    {s.Name}  [{s.Id}]  {s.InstalledVersion ?? "未知版本"}  {s.SourceDisplay ?? s.Source.ToString()}");
        lines.AppendLine("  可升级样例（前 8 条）：");
        foreach (var s in upgrades.Take(8))
            lines.AppendLine($"    {s.Name}  [{s.Id}]  {s.InstalledVersion ?? "?"} → {s.AvailableVersion ?? "?"}");
        lines.AppendLine("  （只列清单，未执行任何安装/升级）");
        return lines.ToString();
    });

    private static Task<string> CheckPnputil() => Task.Run(() =>
    {
        var (code, stdout, stderr) = WindowsDriverUpdateAdapter.MakeRealPnputilRunner()("enum-drivers");
        if (code != 0)
            throw new InvalidOperationException($"pnputil /enum-drivers 退出码 {code}：{stderr.Trim()}");
        var rows = PnputilOutputParser.ParseEnumDrivers(stdout);
        var lines = new StringBuilder();
        lines.AppendLine($"  pnputil 可用，解析出 {rows.Count} 个第三方驱动。");
        foreach (var r in rows.Take(8))
            lines.AppendLine($"    {r.PublishedName}  {r.Provider}  {r.ClassName}  {r.Version}");
        lines.AppendLine($"  高危类（显卡/芯片组/存储/网络）共 {rows.Count(r => DriverCriticality.IsCriticalClass(r.ClassName))} 个。");
        return lines.ToString();
    });

    private static async Task<string> CheckWua()
    {
        var gateway = new WuaComDriverGateway();
        var list = await gateway.SearchDriverUpdatesAsync();
        var lines = new StringBuilder();
        lines.AppendLine($"  WUA 搜索完成，{list.Count} 个驱动更新候选。");
        foreach (var d in list.Take(8))
            lines.AppendLine($"    {d.DeviceName}  [{d.HardwareId}]  {d.Version ?? "?"}  类别 {d.ClassName ?? "?"}");
        lines.AppendLine("  （只搜候选，未执行任何驱动安装）");
        return lines.ToString();
    }
}
