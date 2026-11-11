using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TaoKeeper.Core.Services;

/// <summary>SHA-256 流式哈希实现（跨平台可移植，测试用；正式版接 C++ 核心）。</summary>
public sealed class Sha256FileHasher : IFileHasher
{
    private const int BufferSize = 1024 * 1024; // 1MB

    public async Task<string> HashAsync(string fullPath, CancellationToken cancellation = default)
    {
        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.SequentialScan);

        return await Task.Run(async () =>
        {
            // var hash = await SHA256.HashStreamAsync
            // (net8 无 HashStreamAsync，手写循环)
            using var sha = SHA256.Create();
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellation)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                cancellation.ThrowIfCancellationRequested();
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }, cancellation);
    }
}
