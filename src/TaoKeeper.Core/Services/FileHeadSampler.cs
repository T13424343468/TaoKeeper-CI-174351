using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TaoKeeper.Core.Services;

/// <summary>头部抽样器：读取文件前 N 字节用于初筛（零成本淘汰明显不同的文件）。</summary>
public interface IHeadSampler
{
    /// <summary>读取文件头部 N 字节并转为十六进制串；文件不足 N 字节按实际长度读。</summary>
    Task<string> HeadHexAsync(string fullPath, int bytes, CancellationToken cancellation = default);
}

/// <summary>基于文件流的头部抽样器（跨平台实现；Windows 真机可换 C++ 核心直读版）。</summary>
public sealed class FileHeadSampler : IHeadSampler
{
    public async Task<string> HeadHexAsync(string fullPath, int bytes, CancellationToken cancellation = default)
    {
        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buf = new byte[bytes];
        int read = 0;
        while (read < bytes)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, bytes - read), cancellation);
            if (n == 0) break;
            read += n;
        }
        return Convert.ToHexString(buf, 0, read);
    }
}
