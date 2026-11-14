using System;
using System.Collections.Generic;

namespace TaoKeeper.Core.Modules.Toolbox;

/// <summary>右键菜单条目（只读视图）：位置=注册表挂载点，启用态与出处一目了然。</summary>
public sealed record ContextMenuEntry(
    string KeyPath,        // 完整注册表路径（禁用/启用的操作句柄）
    string LocationLabel,  // 人话位置：文件右键/文件夹右键/桌面右键等
    string Name,           // 菜单显示名（键名；"-" 前缀=已被禁用）
    string? Clsid,         // shellex 处理器的 CLSID（可空）
    string? DllPath,       // 处理器 DLL 实际路径（可查不到如实空）
    bool Enabled);

/// <summary>右键菜单管理通道（R-P10-6 口径）：禁用=键名加 "-" 前缀（可逆、不删数据）；启用=还原去前缀。</summary>
public interface IContextMenuManager
{
    /// <summary>只读扫描：枚举常用挂载点的右键菜单条目。</summary>
    IReadOnlyList<ContextMenuEntry> Scan();

    /// <summary>禁用（可逆：键名加 "-" 前缀，不删除任何键值）。返回是否生效。</summary>
    bool Disable(string keyPath);

    /// <summary>启用（还原：去掉 "-" 前缀）。</summary>
    bool Enable(string keyPath);
}
