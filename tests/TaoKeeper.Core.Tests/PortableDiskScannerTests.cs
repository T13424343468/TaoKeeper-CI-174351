using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Services;
using Xunit;

namespace TaoKeeper.Core.Tests;

public sealed class PortableDiskScannerTests : IDisposable
{
    private readonly string _root;

    public PortableDiskScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "taokeeper-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task QuickScan_FindsFiles_AndCountsBytes()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), new string('x', 1000));
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_root, "sub", "b.bin"), new string('y', 500));

        var scanner = new PortableDiskScanner();
        var result = await scanner.ScanAsync(new[] { _root }, ScanOptions.Defaults(ScanMode.Quick));

        Assert.Equal(2, result.TotalFiles);
        Assert.Equal(1500, result.TotalBytes);
        Assert.Null(result.Files.First(f => f.Name == "a.txt").Sha256); // 快扫不算哈希
    }

    [Fact]
    public async Task QuickScan_SkipsWhitelistedDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(_root, "node_modules", "noise.js"), "noise");
        await File.WriteAllTextAsync(Path.Combine(_root, "keep.txt"), "keep");

        var scanner = new PortableDiskScanner();
        var result = await scanner.ScanAsync(new[] { _root }, ScanOptions.Defaults(ScanMode.Quick));

        Assert.Single(result.Files);
        Assert.Equal("keep.txt", result.Files[0].Name);
    }

    [Fact]
    public async Task FullScan_ComputesHashes_AndFlagsLargeFiles()
    {
        // 生成两个内容完全相同的文件，验证哈希一致（重复文件识别的根基）
        var payload = new byte[4096];
        new Random(42).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_root, "one.bin"), payload);
        await File.WriteAllBytesAsync(Path.Combine(_root, "two.bin"), payload);

        // 大文件：门槛压低到 10 字节便于测试
        var options = new ScanOptions
        {
            ComputeHashes = true,
            LargeFileThresholdBytes = 10
        };
        var scanner = new PortableDiskScanner(new Sha256FileHasher());
        var result = await scanner.ScanAsync(new[] { _root }, options);

        var one = result.Files.First(f => f.Name == "one.bin");
        var two = result.Files.First(f => f.Name == "two.bin");
        Assert.NotNull(one.Sha256);
        Assert.Equal(one.Sha256, two.Sha256);      // 同内容同哈希
        Assert.Equal(2, result.LargeFiles.Count);  // 都超过 10 字节门槛
        Assert.Equal(one.Sha256, result.LargeFiles[0].Sha256);
    }

    [Fact]
    public async Task Scan_CanBeCancelled()
    {
        for (int i = 0; i < 50; i++)
            await File.WriteAllTextAsync(Path.Combine(_root, $"f{i}.txt"), "data");

        var scanner = new PortableDiskScanner();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 直接取消

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(new[] { _root }, ScanOptions.Defaults(ScanMode.Quick), cancellation: cts.Token));
    }

    [Fact]
    public async Task FullScan_LargeFilesExcludedFromHashing_WhenOverLimit()
    {
        // 生成超过 MaxHashFileSizeBytes 的文件，哈希应跳过
        await File.WriteAllTextAsync(Path.Combine(_root, "big.bin"), new string('z', 2048));
        var options = new ScanOptions
        {
            ComputeHashes = true,
            MaxHashFileSizeBytes = 1024 // 上限 1KB，文件 2KB
        };
        var scanner = new PortableDiskScanner(new Sha256FileHasher());
        var result = await scanner.ScanAsync(new[] { _root }, options);

        Assert.Null(result.Files.Single().Sha256);
    }
}
