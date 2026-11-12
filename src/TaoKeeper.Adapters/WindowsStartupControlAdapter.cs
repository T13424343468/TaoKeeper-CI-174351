using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>启动项快照条目：禁用前的原值，供回滚恢复（落盘 JSON，可跨会话还原）。</summary>
public sealed record StartupSnapshotEntry(
    string ItemId,
    StartupItemSource Source,
    /// <summary>RegistryRun：完整键路径；StartupFolder：原文件完整路径。</summary>
    string Location,
    /// <summary>被禁用项展示名。</summary>
    string Name,
    /// <summary>RegistryRun：原注册表值数据；StartupFolder：禁用后文件被移到的新路径。</summary>
    string OriginalValue,
    /// <summary>注册表值类型名（如 REG_SZ）；StartupFolder 为 null。</summary>
    string? RegistryValueKind,
    DateTimeOffset DisabledAtUtc);

/// <summary>
/// Windows 启动项真实适配器（P5 性能加速 / P4 体检启动项维度的真数据源）：
/// 枚举 HKCU/HKLM Run 键 + 当前用户启动文件夹（真实清单），禁用=先快照后摘除、启用=按快照恢复。
/// 红线：只动 Run 键与启动文件夹的用户可逆项；逐项执行、先落盘快照再动系统（断言 A5：快照落盘可回滚）；
/// 计划任务/服务类暂不纳入（清单里不出现即不越界），后续版本再扩。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupControlAdapter : IStartupControlAdapter
{
    private const string RunKeyCurrentUser = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyLocalMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _snapshotPath;

    /// <summary>snapshotPath：快照落盘位置（LocalAppData\TaoKeeper 下，由装配层传入）。</summary>
    public WindowsStartupControlAdapter(string snapshotPath)
    {
        _snapshotPath = snapshotPath ?? throw new ArgumentNullException(nameof(snapshotPath));
        var dir = Path.GetDirectoryName(_snapshotPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>枚举真实启动项（HKCU/HKLM Run 键 + 启动文件夹）。读注册表/枚举失败时跳过该来源，不假数据。</summary>
    public Task<IReadOnlyList<StartupItemEntry>> ListStartupItemsAsync(CancellationToken cancellation = default)
        => Task.FromResult((IReadOnlyList<StartupItemEntry>)ListStartupItems());

    internal IReadOnlyList<StartupItemEntry> ListStartupItems()
    {
        var items = new List<StartupItemEntry>();
        var snapshot = LoadSnapshot();
        var disabledIds = snapshot.Select(s => s.ItemId).ToHashSet(StringComparer.Ordinal);

        items.AddRange(ReadRunKey(Registry.CurrentUser, RunKeyCurrentUser, "注册表当前用户 Run 键", disabledIds));
        items.AddRange(ReadRunKey(Registry.LocalMachine, RunKeyLocalMachine, "注册表系统 Run 键", disabledIds));
        items.AddRange(ReadStartupFolder(disabledIds));

        // 快照里的条目若已不在现清单（键被外部改动），以「已禁用」态呈现——快照即事实源。
        foreach (var entry in snapshot.Where(s => items.All(i => i.Id != s.ItemId)))
        {
            items.Add(new StartupItemEntry(
                entry.ItemId, entry.Name, entry.Source, Publisher: null, Enabled: false,
                ClassifyRisk(entry.OriginalValue), RiskHint: "已在 TaoKeeper 中禁用（快照可一键恢复）",
                Command: entry.OriginalValue, OwningProductId: null));
        }
        return items;
    }

    /// <summary>单项启用/禁用。禁用=快照落盘后摘除（注册表值删除/启动项文件移入禁用目录）；启用=按快照恢复。</summary>
    public Task<StartupToggleResult> ToggleStartupAsync(string itemId, bool enable, CancellationToken cancellation = default)
        => Task.FromResult(enable ? EnableItem(itemId) : DisableItem(itemId));

    private IEnumerable<StartupItemEntry> ReadRunKey(RegistryKey root, string subKey, string sourceLabel, ISet<string> disabledIds)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var entries = new List<(string Name, string Value)>();
        try
        {
            using var key = root.OpenSubKey(subKey, writable: false);
            if (key is null) yield break;
            foreach (var name in key.GetValueNames())
            {
                var value = key.GetValue(name)?.ToString() ?? string.Empty;
                if (value.Length == 0) continue;
                entries.Add((name, value));
            }
        }
        catch (System.Security.SecurityException)
        {
            // HKLM 无读权限（非管理员）：该来源如实缺席，不编清单
        }
        catch (IOException)
        {
        }

        foreach (var (name, value) in entries)
        {
            var id = StartupItemId(sourceLabel, name);
            var enabled = !disabledIds.Contains(id);
            var risk = ClassifyRisk(value);
            yield return new StartupItemEntry(
                id, name, StartupItemSource.RegistryRun, Publisher: null, Enabled: enabled, risk,
                RiskHint: RiskHint(risk, sourceLabel),
                Command: value, OwningProductId: null);
        }
    }

    private IEnumerable<StartupItemEntry> ReadStartupFolder(ISet<string> disabledIds)
    {
        var folder = StartupFolderPath();
        if (folder is null || !Directory.Exists(folder)) yield break;
        var disabledDir = DisabledFolderPath(folder);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly);
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var file in files)
        {
            var id = StartupItemId("启动文件夹", Path.GetFileName(file));
            var enabled = !disabledIds.Contains(id);
            // 风险按命令真实特征分级（ClassifyRisk），不再硬编码 Low——
            // 硬编码 Low 会让启动文件夹项永远进不了加速建议清单，禁用入口形同虚设。
            var risk = ClassifyRisk(file);
            yield return new StartupItemEntry(
                id, Path.GetFileNameWithoutExtension(file), StartupItemSource.StartupFolder, Publisher: null,
                Enabled: enabled, risk,
                RiskHint: RiskHint(risk, "启动文件夹") ?? "启动文件夹内的自启动项，可按需禁用（快照可恢复）",
                Command: file, OwningProductId: null);
        }

        // 禁用目录中的项（快照还原目录）：以已禁用态呈现
        if (!Directory.Exists(disabledDir)) yield break;
        foreach (var file in EnumerateSafe(disabledDir))
        {
            var id = StartupItemId("启动文件夹", Path.GetFileName(file));
            yield return new StartupItemEntry(
                id, Path.GetFileNameWithoutExtension(file), StartupItemSource.StartupFolder, Publisher: null,
                Enabled: false, StartupRiskLevel.Low,
                RiskHint: "已在 TaoKeeper 中禁用（文件已移出启动文件夹，快照可恢复）",
                Command: file, OwningProductId: null);
        }
    }

    private StartupToggleResult DisableItem(string itemId)
    {
        if (!OperatingSystem.IsWindows())
            return new StartupToggleResult(itemId, false, false, "启动项管理仅 Windows 真机可用。");

        var item = ListStartupItems().FirstOrDefault(i => i.Id == itemId);
        if (item is null) return new StartupToggleResult(itemId, false, false, "启动项不存在或已被移除。");
        if (!item.Enabled) return new StartupToggleResult(itemId, false, false, "该项已处于禁用状态。");

        try
        {
            var entry = item.Source switch
            {
                StartupItemSource.RegistryRun => DisableRegistryRun(item),
                StartupItemSource.StartupFolder => DisableStartupFolder(item),
                _ => null,
            };
            if (entry is null)
                return new StartupToggleResult(itemId, false, false, "该来源暂不支持禁用（计划任务/服务类不越界改动）。");

            SaveSnapshot(entry); // 先落盘快照再动系统：断电/崩溃后快照仍在，可回滚
            return new StartupToggleResult(itemId, false, true, "已禁用，下次开机生效；快照已落盘可随时恢复。");
        }
        catch (UnauthorizedAccessException)
        {
            return new StartupToggleResult(itemId, false, false, "权限不足（系统级启动项需要管理员权限），未做任何改动。");
        }
        catch (IOException ex)
        {
            return new StartupToggleResult(itemId, false, false, $"禁用未完成：{ex.Message}");
        }
    }

    private StartupToggleResult EnableItem(string itemId)
    {
        if (!OperatingSystem.IsWindows())
            return new StartupToggleResult(itemId, true, false, "启动项管理仅 Windows 真机可用。");

        var snapshot = LoadSnapshot();
        var entry = snapshot.FirstOrDefault(s => s.ItemId == itemId);
        if (entry is null)
            return new StartupToggleResult(itemId, true, false, "未找到该项的禁用快照，无法恢复（可能从未经 TaoKeeper 禁用）。");

        try
        {
            switch (entry.Source)
            {
                case StartupItemSource.RegistryRun:
                    // 恢复写回快照记录的原键（entry.Location）与原值名（entry.Name），
                    // 不再写回 ItemId 指纹名——否则原值丢失、回滚语义破坏。
                    var (restoreRoot, restoreSubKey) = ResolveRunKey(entry.Location);
                    using (var key = restoreRoot.OpenSubKey(restoreSubKey, writable: true))
                    {
                        if (key is null)
                            return new StartupToggleResult(itemId, true, false, "注册表 Run 键不可写（权限不足），未做任何改动。");
                        var kind = entry.RegistryValueKind switch
                        {
                            "REG_EXPAND_SZ" => RegistryValueKind.ExpandString,
                            _ => RegistryValueKind.String,
                        };
                        key.SetValue(entry.Name, entry.OriginalValue, kind);
                    }
                    break;
                case StartupItemSource.StartupFolder:
                    var folder = StartupFolderPath();
                    var disabledDir = folder is null ? null : DisabledFolderPath(folder);
                    if (folder is null || disabledDir is null)
                        return new StartupToggleResult(itemId, true, false, "启动文件夹定位失败，未做任何改动。");
                    Directory.CreateDirectory(folder);
                    if (File.Exists(entry.OriginalValue))
                        File.Move(entry.OriginalValue, Path.Combine(folder, Path.GetFileName(entry.OriginalValue)), overwrite: false);
                    break;
            }

            RemoveSnapshot(itemId);
            return new StartupToggleResult(itemId, true, true, "已恢复启用，下次开机生效。");
        }
        catch (UnauthorizedAccessException)
        {
            return new StartupToggleResult(itemId, true, false, "权限不足，恢复未完成；快照仍在，可重试。");
        }
        catch (IOException ex)
        {
            return new StartupToggleResult(itemId, true, false, $"恢复未完成：{ex.Message}（快照仍在）");
        }
    }

    private StartupSnapshotEntry? DisableRegistryRun(StartupItemEntry item)
    {
        // 禁用按真实注册表值名（item.Name=枚举时的值名）查删；ItemId 是展示用指纹（哈希），不是值名。
        var name = item.Name;
        foreach (var (root, subKey) in new[] { (Registry.CurrentUser, RunKeyCurrentUser), (Registry.LocalMachine, RunKeyLocalMachine) })
        {
            try
            {
                using var key = root.OpenSubKey(subKey, writable: true);
                if (key is null) continue;
                var value = key.GetValue(name);
                if (value is null) continue;
                var kind = key.GetValueKind(name);
                key.DeleteValue(name, throwOnMissingValue: false);
                return new StartupSnapshotEntry(
                    item.Id, StartupItemSource.RegistryRun, subKey, item.Name, value.ToString() ?? string.Empty,
                    kind.ToString(), DateTimeOffset.UtcNow);
            }
            catch (System.Security.SecurityException)
            {
                continue; // HKLM 无写权限：跳过，该项禁用交由用户以管理员运行后重试
            }
        }
        return null;
    }

    private StartupSnapshotEntry? DisableStartupFolder(StartupItemEntry item)
    {
        var folder = StartupFolderPath();
        if (folder is null || !File.Exists(item.Command)) return null;
        var disabledDir = DisabledFolderPath(folder);
        Directory.CreateDirectory(disabledDir);
        var movedPath = Path.Combine(disabledDir, Path.GetFileName(item.Command));
        File.Move(item.Command, movedPath, overwrite: true);
        return new StartupSnapshotEntry(
            item.Id, StartupItemSource.StartupFolder, folder, item.Name, movedPath,
            RegistryValueKind: null, DateTimeOffset.UtcNow);
    }

    private List<StartupSnapshotEntry> LoadSnapshot()
    {
        try
        {
            if (!File.Exists(_snapshotPath)) return new List<StartupSnapshotEntry>();
            return JsonSerializer.Deserialize<List<StartupSnapshotEntry>>(File.ReadAllText(_snapshotPath), JsonOpts)
                   ?? new List<StartupSnapshotEntry>();
        }
        catch (JsonException)
        {
            try { File.Copy(_snapshotPath, _snapshotPath + ".corrupt", overwrite: true); } catch (IOException) { }
            return new List<StartupSnapshotEntry>();
        }
        catch (IOException)
        {
            return new List<StartupSnapshotEntry>();
        }
    }

    private void SaveSnapshot(StartupSnapshotEntry entry)
    {
        var list = LoadSnapshot();
        list.RemoveAll(s => s.ItemId == entry.ItemId);
        list.Add(entry);
        var tmp = _snapshotPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts));
        File.Move(tmp, _snapshotPath, overwrite: true);
    }

    private void RemoveSnapshot(string itemId)
    {
        var list = LoadSnapshot();
        if (list.RemoveAll(s => s.ItemId == itemId) == 0) return;
        var tmp = _snapshotPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts));
        File.Move(tmp, _snapshotPath, overwrite: true);
    }

    private static string StartupItemId(string sourceLabel, string name)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
               System.Text.Encoding.UTF8.GetBytes(sourceLabel + "|" + name)))[..24].ToLowerInvariant();

    /// <summary>按快照记录的键路径解析回写目标（HKLM/HKCU 兼容；未知路径按 HKCU 处理）。</summary>
    private static (RegistryKey Root, string SubKey) ResolveRunKey(string location)
        => string.Equals(location, RunKeyLocalMachine, StringComparison.OrdinalIgnoreCase)
            ? (Registry.LocalMachine, RunKeyLocalMachine)
            : (Registry.CurrentUser, RunKeyCurrentUser);

    private static string? StartupFolderPath() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup))
        : null;

    private static string DisabledFolderPath(string startupFolder) =>
        Path.Combine(Path.GetDirectoryName(startupFolder) ?? startupFolder, "TaoKeeperDisabled");

    private static IEnumerable<string> EnumerateSafe(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList(); }
        catch (IOException) { return new List<string>(); }
        catch (UnauthorizedAccessException) { return new List<string>(); }
    }

    private static StartupRiskLevel ClassifyRisk(string command)
    {
        if (string.IsNullOrEmpty(command)) return StartupRiskLevel.Low;
        var cmd = command.ToLowerInvariant();
        string[] promoHints = { "baidu.com/goto", "hao.", "123.com", "wps_", "toutiao", "kweishi", "推广", "rush" };
        string[] updaterHints = { "update", "autoupdate", "updater", "agent", "helper", "assistant", "liveupdate" };
        if (promoHints.Any(h => cmd.Contains(h))) return StartupRiskLevel.High;
        if (updaterHints.Any(h => cmd.Contains(h))) return StartupRiskLevel.Medium;
        return StartupRiskLevel.Low;
    }

    private static string? RiskHint(StartupRiskLevel risk, string sourceLabel) => risk switch
    {
        StartupRiskLevel.High => "命令特征疑似推广位，建议禁用（需你确认后执行）",
        StartupRiskLevel.Medium => "疑似自动更新/助手类组件，可自行判断",
        _ => null,
    };
}

/// <summary>
/// 开机耗时读数（P5-2 口径）：系统事件日志 Microsoft-Windows-Diagnostics-Performance/Operational
/// 事件 ID 100（启动完成），取 MainPathBootTime + BootPostBootTime（毫秒）。
/// 只读；日志不可读（非管理员/精简系统）时返回空清单，调用方按未测态如实展示。
/// </summary>
public sealed class BootTimeReader
{
    public const string PerformanceLogName = "Microsoft-Windows-Diagnostics-Performance/Operational";

    /// <summary>读最近 count 次开机的耗时（毫秒，按时间新→旧）。读不到如实返回空清单。</summary>
    public IReadOnlyList<long> ReadRecentBootTimesMs(int count = 3)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<long>();
        try
        {
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                PerformanceLogName, System.Diagnostics.Eventing.Reader.PathType.LogName,
                $"*[System[(EventID=100)]]");
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            var results = new List<long>();
            while (results.Count < count)
            {
                using var evt = reader.ReadEvent();
                if (evt is null) break;
                long main = 0, post = 0;
                foreach (var prop in evt.Properties)
                {
                    // 事件 100 的 properties 顺序固定：含 MainPathBootTime 与 BootPostBootTime（毫秒）
                    if (long.TryParse(prop.Value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ms))
                    {
                        if (main == 0 && ms > 100) main = ms;
                        else if (ms > 0) post = ms;
                    }
                }
                if (main > 0) results.Add(main + post);
            }
            return results;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                       or System.Diagnostics.Eventing.Reader.EventLogException
                                       or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            return Array.Empty<long>(); // 日志不可读：未测态，不假数据
        }
    }

    /// <summary>三次中位数口径（P5-2）；不足 3 次取实际可得的中位数。</summary>
    public long? MedianBootTimeMs()
    {
        var times = ReadRecentBootTimesMs(3).OrderBy(t => t).ToList();
        if (times.Count == 0) return null;
        return times[times.Count / 2];
    }
}
