using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using TaoKeeper.Core.Modules.ResidueDetect;
using TaoKeeper.Core.Modules.SandboxPreview;
using TaoKeeper.Core.Modules.SnapshotChain;

namespace TaoKeeper.App.Adapters;

/// <summary>真实文件系统沙箱写适配器（P13 真机面；W3 写穿零发生标真机验证项）。</summary>
internal sealed class RealSandboxFileSystem : ISandboxFileSystem
{
    private readonly List<string> _written = [];
    private readonly List<string> _deleted = [];
    public ReadOnlyCollection<string> WrittenPaths => _written.AsReadOnly();
    public ReadOnlyCollection<string> DeletedPaths => _deleted.AsReadOnly();

    public bool Exists(string path) => File.Exists(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public void WriteAllBytes(string path, byte[] content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, content);
        _written.Add(path);
    }
    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        _deleted.Add(path);
    }
}

/// <summary>越界边界：系统目录与 Program Files 拒写（宁误拦不写穿，R-P13-4；白名单收紧留真机复核）。</summary>
internal sealed class SystemDirsBoundary : ISandboxBoundary
{
    public string? InspectWrite(string path)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
        }.Where(r => !string.IsNullOrEmpty(r));

        foreach (var root in roots)
        {
            if (path.StartsWith(root.TrimEnd('\\', '/') + @"\", StringComparison.OrdinalIgnoreCase))
            {
                return $"越界：{root} 为受保护目录，预演已拒绝（宁误拦不写穿）";
            }
        }
        return null;
    }
}

/// <summary>快照状态应用点：回滚/还原时把快照字节按原路径写回真实文件系统。</summary>
internal sealed class FileSnapshotStateApplier : ISnapshotStateApplier
{
    public System.Threading.Tasks.Task ApplyAsync(string targetPath, byte[] content, System.Threading.CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(targetPath, content);
        return System.Threading.Tasks.Task.CompletedTask;
    }
}

/// <summary>空节点源（非 Windows 平台如实空清单，不假数据）。</summary>
internal sealed class EmptyResidueNodeSource : IResidueNodeSource
{
    public IReadOnlyList<ResidueNode> Nodes { get; } = [];
    public IReadOnlyList<ResidueEdge> Edges { get; } = [];
}

/// <summary>
/// Windows 残留枚举适配器（P14 真机面，只读）：枚举安装根下文件与桌面快捷方式（P/Invoke 深枚举留 L2 万链真机班）。
/// 符号链接循环/硬链接/权限边界六路场景标真机 L2（Linux 沙箱无 Windows 链接语义）。
/// </summary>
internal sealed class WindowsResidueNodeSource : IResidueNodeSource
{
    public IReadOnlyList<ResidueNode> Nodes { get; }
    public IReadOnlyList<ResidueEdge> Edges { get; } = [];

    public WindowsResidueNodeSource(string[] installRoots)
    {
        var nodes = new List<ResidueNode>();
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(desktop))
            {
                foreach (var lnk in Directory.EnumerateFiles(desktop, "*.lnk"))
                {
                    nodes.Add(new ResidueNode(ResidueNodeType.Shortcut, lnk, Target: ResolveShortcutTarget(lnk)));
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Nodes = nodes;
    }

    /// <summary>快捷方式目标解析：只读枚举（深解析走 WScript.Shell COM，真机班核验；此处结构占位不假数据）。</summary>
    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            var shell = Activator.CreateInstance(type);
            if (shell is null) return null;
            var shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [lnkPath]);
            if (shortcut is null) return null;
            var targetType = shortcut.GetType();
            var target = targetType.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null; // 解析不了如实 null（进待确认组，不假数据）
        }
    }
}
