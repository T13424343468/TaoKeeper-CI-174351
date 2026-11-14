using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using TaoKeeper.Core.Modules.Toolbox;

namespace TaoKeeper.Adapters;

/// <summary>
/// Windows 右键菜单适配器（R-P10-6）：只读扫描 + 可逆禁用/启用（键名 "-" 前缀法，标准工具同口径）。
/// 非静默删除：任何写动作都是键重命名（数据不丢），真删由用户明确要求时另走确认。
/// </summary>
public sealed class WindowsContextMenuAdapter : IContextMenuManager
{
    // 挂载点：注册表根\子键路径 → 人话位置
    private static readonly (string Root, string Relative, string Label)[] Mounts =
    {
        (Registry.ClassesRoot.Name, @"*\shellex\ContextMenuHandlers", "文件右键"),
        (Registry.ClassesRoot.Name, @"*\shell", "文件右键"),
        (Registry.ClassesRoot.Name, @"Directory\shellex\ContextMenuHandlers", "文件夹右键"),
        (Registry.ClassesRoot.Name, @"Directory\shell", "文件夹右键"),
        (Registry.ClassesRoot.Name, @"Folder\shell", "文件夹右键"),
        (Registry.ClassesRoot.Name, @"Directory\Background\shell", "桌面右键"),
    };

    public IReadOnlyList<ContextMenuEntry> Scan()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var entries = new List<ContextMenuEntry>();
        foreach (var (root, relative, label) in Mounts)
        {
            using var mount = SafeOpen(root, relative);
            if (mount is null) continue;
            foreach (var sub in mount.GetSubKeyNames())
            {
                using var item = SafeOpen(root, $"{relative}\\{sub}");
                if (item is null) continue;
                var clsid = item.GetValue(string.Empty) as string;
                if (clsid is null && item.GetSubKeyNames().Contains("command"))
                    clsid = item.OpenSubKey("command")?.GetValue(string.Empty) as string;
                string? dll = null;
                if (Guid.TryParse(clsid?.Trim('{', '}'), out _))
                {
                    using var clsidKey = SafeOpen(Registry.ClassesRoot.Name, $@"CLSID\{clsid}\InprocServer32");
                    dll = clsidKey?.GetValue(string.Empty) as string;
                }
                entries.Add(new ContextMenuEntry(
                    KeyPath: $"{root}\\{relative}\\{sub}",
                    LocationLabel: label,
                    Name: sub,
                    Clsid: clsid,
                    DllPath: dll,
                    Enabled: !sub.StartsWith("-", StringComparison.Ordinal)));
            }
        }
        return entries;
    }

    public bool Disable(string keyPath) => RenameKey(keyPath, prefix: true);

    public bool Enable(string keyPath) => RenameKey(keyPath, prefix: false);

    private static RegistryKey? SafeOpen(string root, string relative)
    {
        try
        {
            using var rootKey = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default);
            return rootKey.OpenSubKey(relative, writable: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
        {
            return null;
        }
    }

    private static bool RenameKey(string keyPath, bool prefix)
    {
        if (!OperatingSystem.IsWindows()) return false;
        // 键重命名=建新键（连同子键树拷贝）→ 删旧键。数据不丢；失败即抛，页面如实提示。
        var parts = keyPath.Split('\\');
        if (parts.Length < 2) return false;
        var name = parts[^1];
        var parentPath = string.Join('\\', parts[..^1]);
        var newName = prefix ? $"-{name}" : name.TrimStart('-');
        if (string.IsNullOrEmpty(newName) || newName == name) return false;

        using var rootKey = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default);
        using var parent = rootKey.OpenSubKey(parentPath, writable: true);
        if (parent is null) return false;
        using (var source = parent.OpenSubKey(name))
        {
            if (source is null) return false;
            using var target = parent.CreateSubKey(newName, writable: true);
            CopyKeyTree(source, target);
        }
        parent.DeleteSubKeyTree(name);
        return true;
    }

    private static void CopyKeyTree(RegistryKey source, RegistryKey target)
    {
        foreach (var name in source.GetValueNames())
            target.SetValue(string.IsNullOrEmpty(name) ? string.Empty : name, source.GetValue(name) ?? string.Empty);
        foreach (var sub in source.GetSubKeyNames())
        {
            using var s = source.OpenSubKey(sub);
            if (s is null) continue;
            using var t = target.CreateSubKey(sub, writable: true);
            CopyKeyTree(s, t);
        }
    }
}
