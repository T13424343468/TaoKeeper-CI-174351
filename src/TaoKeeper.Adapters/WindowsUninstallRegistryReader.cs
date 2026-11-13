using System;
using System.Collections.Generic;
using Microsoft.Win32;
using TaoKeeper.Adapters;

namespace TaoKeeper.Adapters;

/// <summary>
/// Windows 注册表真机读取（IUninstallRegistryReader 的唯一真实现）。
/// 只读：RegistryKey.OpenSubKey，零写入；三处标准卸载键逐一遍历。
/// 非 Windows 运行时报红不静默跳过（与 winget 适配层同一互操作规则）。
/// 单键损坏/值缺失按空登记跳过——注册表个别烂键不该挡住整份清单。
/// </summary>
public sealed class WindowsUninstallRegistryReader : IUninstallRegistryReader
{
    public IReadOnlyList<RegistryUninstallRow> ReadUninstallKeys()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("注册表已装枚举仅支持 Windows 真机运行；沙箱测试请注入假 IUninstallRegistryReader");

        var rows = new List<RegistryUninstallRow>();
        foreach (var rootPath in RegistryInstalledSoftwareSource.StandardUninstallKeys)
        {
            EnumerateRoot(rootPath, rows);
        }
        return rows;
    }

    private static void EnumerateRoot(string rootPath, List<RegistryUninstallRow> rows)
    {
        // 拆根键与子路径（Registry.RootKeys 只认 RegistryKey 实例，路径串由本层解析）
        var sep = rootPath.IndexOf('\\');
        if (sep < 0) return;
        var subPath = rootPath[(sep + 1)..];
        using var root = RootOf(rootPath[..sep])?.OpenSubKey(subPath);
        if (root is null) return;

        foreach (var keyName in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(keyName);
                if (key is null) continue;
                rows.Add(new RegistryUninstallRow(
                    DisplayName: key.GetValue("DisplayName") as string ?? string.Empty,
                    DisplayVersion: key.GetValue("DisplayVersion") as string,
                    Publisher: key.GetValue("Publisher") as string,
                    UninstallString: key.GetValue("UninstallString") as string,
                    InstallLocation: key.GetValue("InstallLocation") as string,
                    EstimatedSizeKb: key.GetValue("EstimatedSize") is int kb ? kb : null,
                    IsSystemComponent: key.GetValue("SystemComponent") is int sc && sc == 1,
                    RowKey: $"{rootPath}|{keyName}"));
            }
            catch (System.Security.SecurityException) { /* 权限不足的键如实跳过，不挡清单 */ }
            catch (System.IO.IOException) { /* 烂键如实跳过 */ }
        }
    }

    private static RegistryKey? RootOf(string name) => name.ToUpperInvariant() switch
    {
        "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
        "HKEY_CURRENT_USER" => Registry.CurrentUser,
        _ => null,
    };
}
