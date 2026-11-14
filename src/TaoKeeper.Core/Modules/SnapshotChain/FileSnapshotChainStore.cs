using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaoKeeper.Core.Modules.SnapshotChain;

/// <summary>
/// 基于目录的快照链存储（契约第六节冻结布局）：
/// 根目录下每事务一个子目录，含 intent.json、payload/（内容哈希命名，SHA-256）、done.marker、rollback.marker。
/// 所有 JSON 带 schemaVersion=1；读入高版本未知格式时报错拒绝（不静默猜测兼容）。
/// 序列化统一 UTF-8。序列号水位文件 atomic 写（temp + rename）。
/// Corrupt/Overwrite 类成员仅供 L6 断电注入（IRecoveryProbe）与测试使用，正常流程不触碰。
/// </summary>
public sealed class FileSnapshotChainStore : ISnapshotChainStore
{
    private const string IntentFileName = "intent.json";
    private const string PayloadDirName = "payload";
    private const string DoneMarkerName = "done.marker";
    private const string RollbackMarkerName = "rollback.marker";
    private const string SequenceFileName = "sequence.txt";
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _root;

    public FileSnapshotChainStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = rootDirectory;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(PayloadRoot);
    }

    /// <summary>存储根目录（注入用探针/测试读取）。</summary>
    public string RootDirectory => _root;

    private string PayloadRoot => Path.Combine(_root, PayloadDirName);

    private string TransactionDirectory(string transactionId) =>
        Path.Combine(PayloadRoot, transactionId);

    private string IntentPath(string transactionId) =>
        Path.Combine(TransactionDirectory(transactionId), IntentFileName);

    private string PayloadPath(string transactionId, string contentHash) =>
        Path.Combine(TransactionDirectory(transactionId), PayloadDirName, contentHash);

    private string MarkerPath(string transactionId, string markerName) =>
        Path.Combine(PayloadRoot, transactionId, markerName);

    public async Task WriteIntentAsync(string transactionId, JournalEntry entry, CancellationToken ct)
    {
        Directory.CreateDirectory(TransactionDirectory(transactionId));
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        await File.WriteAllTextAsync(IntentPath(transactionId), json, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    public async Task<JournalEntry?> ReadIntentAsync(string transactionId, CancellationToken ct)
    {
        var path = IntentPath(transactionId);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var version = doc.RootElement.GetProperty("schemaVersion").GetInt32();
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Snapshot intent schemaVersion {version} is higher than supported {CurrentSchemaVersion}; refusing to process (contract §6.2).");
        }

        return JsonSerializer.Deserialize<JournalEntry>(json, JsonOptions);
    }

    public async Task WritePayloadAsync(string transactionId, SnapshotItem item, string contentHash, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PayloadPath(transactionId, contentHash))!);
        await File.WriteAllBytesAsync(PayloadPath(transactionId, contentHash), item.Content, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string ContentHash, byte[] Content)>> ReadAllPayloadAsync(string transactionId, CancellationToken ct)
    {
        var dir = Path.Combine(TransactionDirectory(transactionId), PayloadDirName);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var result = new List<(string, byte[])>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var content = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            result.Add((Path.GetFileName(file), content));
        }

        return result;
    }

    public async Task OverwritePayloadInPlaceAsync(string transactionId, string contentHash, byte[] tampered, CancellationToken ct) =>
        await File.WriteAllBytesAsync(PayloadPath(transactionId, contentHash), tampered, ct).ConfigureAwait(false);

    public async Task<bool> DeletePayloadAsync(string transactionId, string contentHash, CancellationToken ct)
    {
        var file = PayloadPath(transactionId, contentHash);
        if (!File.Exists(file))
        {
            return true;
        }

        await Task.Yield();
        File.Delete(file);
        return true;
    }

    public async Task OverwriteIntentAsync(string transactionId, string rawJson, CancellationToken ct) =>
        await File.WriteAllTextAsync(IntentPath(transactionId), rawJson, new UTF8Encoding(false), ct).ConfigureAwait(false);

    public async Task WriteDoneMarkerAsync(string transactionId, CancellationToken ct) =>
        await File.WriteAllTextAsync(MarkerPath(transactionId, DoneMarkerName), "done", new UTF8Encoding(false), ct).ConfigureAwait(false);

    public Task<bool> HasDoneMarkerAsync(string transactionId, CancellationToken ct) =>
        Task.FromResult(File.Exists(MarkerPath(transactionId, DoneMarkerName)));

    public async Task WriteRollbackMarkerAsync(string transactionId, CancellationToken ct) =>
        await File.WriteAllTextAsync(MarkerPath(transactionId, RollbackMarkerName), "rolled-back", new UTF8Encoding(false), ct).ConfigureAwait(false);

    public Task<bool> HasRollbackMarkerAsync(string transactionId, CancellationToken ct) =>
        Task.FromResult(File.Exists(MarkerPath(transactionId, RollbackMarkerName)));

    public Task<IReadOnlyList<string>> ListTransactionIdsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(PayloadRoot))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var ids = Directory.EnumerateDirectories(PayloadRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    public async Task<long> LoadSequenceHighWaterAsync(CancellationToken ct)
    {
        var path = Path.Combine(_root, SequenceFileName);
        if (!File.Exists(path))
        {
            return 0;
        }

        var text = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim();
        return long.TryParse(text, out var value) ? value : 0;
    }

    public async Task SaveSequenceHighWaterAsync(long value, CancellationToken ct)
    {
        var path = Path.Combine(_root, SequenceFileName);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, value.ToString(System.Globalization.CultureInfo.InvariantCulture), new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    internal static string ComputeSha256(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
