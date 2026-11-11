using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TaoKeeper.Core.Services;

/// <summary>文件哈希服务（SHA-256，流式，不整读进内存）。</summary>
public interface IFileHasher
{
    /// <summary>计算文件哈希；文件在计算中被修改或不可读时抛出 IOException。</summary>
    Task<string> HashAsync(string fullPath, CancellationToken cancellation = default);
}
