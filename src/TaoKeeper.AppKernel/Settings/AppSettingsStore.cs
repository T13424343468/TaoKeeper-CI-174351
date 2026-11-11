using System.Text.Json;
using TaoKeeper.AppKernel.Theming;

namespace TaoKeeper.AppKernel.Settings;

/// <summary>
/// 设置持久化：JSON 原子写入（临时文件 + 替换），损坏文件回退默认值并保留 .corrupt 副本。
/// 红线项不在模型内，序列化天然不含红线开关。
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public string FilePath { get; }

    public AppSettingsStore(string filePath) => FilePath = filePath;

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
            return s ?? new AppSettings();
        }
        catch (JsonException)
        {
            try { File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); } catch (IOException) { }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
