using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>
/// 真删适配器（0.2 分层语义落地的真实通道）：
/// 删前把目标完整复制到快照目录（DataDir/delete-snapshots/&lt;id&gt;/，期内可还原），
/// 快照成功后才物理删除原文件/目录；快照失败绝不动原文件，删除失败时快照仍在（如实报失败）。
/// </summary>
public sealed class BackupThenDeleteAdapter : IPermanentRemovalAdapter
{
    private readonly string _snapshotRoot;

    public BackupThenDeleteAdapter(string snapshotRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotRoot);
        _snapshotRoot = snapshotRoot;
    }

    public async Task<PermanentRemovalResult> SnapshotThenDeleteAsync(string fullPath, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        try
        {
            var id = $"del-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
            var destDir = Path.Combine(_snapshotRoot, id);
            Directory.CreateDirectory(destDir);

            if (Directory.Exists(fullPath))
            {
                var rootName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, '/'));
                if (string.IsNullOrEmpty(rootName)) rootName = "dir";
                var dest = Path.Combine(destDir, rootName);
                await Task.Run(() => CopyDirectory(fullPath, dest), cancellation);
                Directory.Delete(fullPath, recursive: true);
                return new PermanentRemovalResult(true, dest);
            }

            if (File.Exists(fullPath))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(fullPath));
                await Task.Run(() => File.Copy(fullPath, dest, overwrite: true), cancellation);
                File.Delete(fullPath);
                return new PermanentRemovalResult(true, dest);
            }

            return new PermanentRemovalResult(false, null, "目标不存在，未执行真删。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return new PermanentRemovalResult(false, null,
                $"快照或删除未完成：{ex.Message}。失败条目未计入已清空间，可重试或改走回收站。");
        }
    }

    /// <summary>整目录复制（保留相对结构）；目标目录已存在时合并覆盖。</summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, relative);
            var destParent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destParent)) Directory.CreateDirectory(destParent);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
