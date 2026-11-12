using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace TaoKeeper.Adapters;

/// <summary>页面文件改动前的原值快照（落盘 JSON，回滚依据）。</summary>
public sealed record PageFileSnapshot(string[] OriginalEntries, DateTimeOffset SavedAtUtc);

/// <summary>
/// Windows 页面文件真实适配器（M9/P-D2）：
/// 读/写 HKLM Session Manager\Memory Management\PagingFiles（REG_MULTI_SZ），改动前先落盘快照。
/// 红线：只在用户三步确认后的执行器里被调用；写前必备份、快照可一键回滚；未配置/不可读写如实上报，不假成功。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32PageFileApplyAdapter
{
    private const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string PagingFilesValue = "PagingFiles";

    private readonly string _snapshotPath;

    /// <summary>snapshotPath：快照落盘位置（LocalAppData\TaoKeeper 下，由装配层传入；缺省用约定路径）。</summary>
    public Win32PageFileApplyAdapter(string? snapshotPath = null)
    {
        _snapshotPath = snapshotPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaoKeeper", "pagefile-snapshot.json");
    }

    /// <summary>读当前 PagingFiles 多串；键不存在返回空数组（未配置），读失败抛出由调用方如实转述。</summary>
    public string[] ReadCurrentPagingFiles()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MemoryManagementKey, writable: false)
            ?? throw new InvalidOperationException("注册表 Memory Management 键不可读（权限不足或环境异常）。");
        return (key.GetValue(PagingFilesValue) as string[]) ?? Array.Empty<string>();
    }

    /// <summary>改动前备份：把当前 PagingFiles 落盘（每次应用都覆盖为「改前」快照，保证回滚语义）。</summary>
    public void BackupCurrent(string[] currentEntries)
    {
        var dir = Path.GetDirectoryName(_snapshotPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var snapshot = new PageFileSnapshot(currentEntries, DateTimeOffset.UtcNow);
        File.WriteAllText(_snapshotPath, JsonSerializer.Serialize(snapshot), System.Text.Encoding.UTF8);
    }

    /// <summary>写目标条目（REG_MULTI_SZ）。重启后生效，本方法不做即时生效承诺。</summary>
    public void ApplyEntries(string[] targetEntries)
    {
        using var key = Registry.LocalMachine.OpenSubKey(MemoryManagementKey, writable: true)
            ?? throw new UnauthorizedAccessException("注册表 Memory Management 键不可写（需要管理员权限）。");
        key.SetValue(PagingFilesValue, targetEntries, RegistryValueKind.MultiString);
    }

    /// <summary>是否有可回滚快照。</summary>
    public bool HasBackup => File.Exists(_snapshotPath);

    /// <summary>按快照回滚（恢复改前 PagingFiles）；无快照抛 InvalidOperationException。</summary>
    public void Rollback()
    {
        if (!HasBackup) throw new InvalidOperationException("没有页面文件快照，无法回滚。");
        var snapshot = JsonSerializer.Deserialize<PageFileSnapshot>(File.ReadAllText(_snapshotPath))
            ?? throw new InvalidOperationException("页面文件快照损坏，拒绝回滚（快照仍在，请人工核对）。");
        ApplyEntries(snapshot.OriginalEntries);
    }

    /// <summary>快照位置（回执与诊断用）。</summary>
    public string SnapshotPath => _snapshotPath;
}
