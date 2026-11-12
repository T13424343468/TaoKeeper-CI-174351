using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>
/// 弹窗管理配置持久化（P8）：规则清单 / 白名单 / 拦截记录 三份 JSON 原子落盘
/// （临时文件 + 替换，与 AppSettingsStore 同口径；损坏回退默认值并保留 .corrupt 副本）。
/// 全部本地文件，零外发（红线 R-2/N8）。
/// </summary>
public sealed class PopupConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _rulesPath;
    private readonly string _whitelistPath;
    private readonly string _recordsPath;

    public PopupConfigStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _rulesPath = Path.Combine(directory, "popup-rules.json");
        _whitelistPath = Path.Combine(directory, "popup-whitelist.json");
        _recordsPath = Path.Combine(directory, "popup-records.json");
    }

    public IReadOnlyList<PopupRule> LoadRules() => LoadList<PopupRule>(_rulesPath);

    public void SaveRules(IEnumerable<PopupRule> rules) => SaveList(rules, _rulesPath);

    public IReadOnlyList<PopupWhitelistEntry> LoadWhitelist() => LoadList<PopupWhitelistEntry>(_whitelistPath);

    public void SaveWhitelist(IEnumerable<PopupWhitelistEntry> whitelist) => SaveList(whitelist, _whitelistPath);

    public IReadOnlyList<PopupInterceptionRecord> LoadRecords() => LoadList<PopupInterceptionRecord>(_recordsPath);

    public void SaveRecords(IEnumerable<PopupInterceptionRecord> records) => SaveList(records, _recordsPath);

    private static List<T> LoadList<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<T>();
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOpts) ?? new List<T>();
        }
        catch (JsonException)
        {
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch (IOException) { }
            return new List<T>();
        }
        catch (IOException)
        {
            return new List<T>();
        }
    }

    private static void SaveList<T>(IEnumerable<T> items, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// 存储感知方案持久化（P9）：StorageSensePlan JSON 原子落盘，重启后方案与上次运行锚点不丢。
/// 默认值=引擎 Default（关、无范围），「默认关闭」断言（P9-1/B4）由引擎保证，存储层只忠实回放。
/// </summary>
public sealed class StorageSenseStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public StorageSenseStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "storage-sense-plan.json");
    }

    public StorageSensePlan Load()
    {
        try
        {
            if (!File.Exists(_path)) return StorageSensePlan.Default;
            return JsonSerializer.Deserialize<StorageSensePlan>(File.ReadAllText(_path), JsonOpts)
                   ?? StorageSensePlan.Default;
        }
        catch (JsonException)
        {
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch (IOException) { }
            return StorageSensePlan.Default;
        }
        catch (IOException)
        {
            return StorageSensePlan.Default;
        }
    }

    public void Save(StorageSensePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(plan, JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }
}
