using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Services;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>重复文件识别：零误报是硬底线，任何文件名/时间相同但内容不同的都不得误判。</summary>
public class DuplicateFileFinderTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "taokeeper-dup-tests");

    private static string PathOf(string name) => Path.Combine(Root, name);

    private static void WriteAllBytesDir(string name, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(name))!);
        File.WriteAllBytes(PathOf(name), data);
    }

    public DuplicateFileFinderTests() => Directory.CreateDirectory(Root);

    private static void Cleanup()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    [Fact]
    public async Task 同内容不同名文件识别为重复()
    {
        Cleanup();
        var data = new byte[8192];
        new Random(42).NextBytes(data);
        WriteAllBytesDir("副本a.bin", data);
        WriteAllBytesDir("sub/另一个名字.bin", data);

        var scanner = new PortableDiskScanner(new Sha256FileHasher());
        var scan = await scanner.ScanAsync(new[] { Root }, new ScanOptions { ComputeHashes = true });
        var finder = new DuplicateFileFinder(new Sha256FileHasher(), new FileHeadSampler());
        var groups = await finder.FindAsync(scan.Files);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(8192, group.SizeBytes);
        Assert.Equal(8192, group.ReclaimableBytes); // 留一份，可释放一份
    }

    [Fact]
    public async Task 同名同体积不同内容绝不误报()
    {
        Cleanup();
        var a = new byte[4096]; new Random(1).NextBytes(a);
        var b = new byte[4096]; new Random(2).NextBytes(b);
        Assert.NotEqual(a, b);
        WriteAllBytesDir("同名文件.bin", a);
        WriteAllBytesDir("sub/同名文件.bin", b);

        var scanner = new PortableDiskScanner(new Sha256FileHasher());
        var scan = await scanner.ScanAsync(new[] { Root }, new ScanOptions { ComputeHashes = true });
        var finder = new DuplicateFileFinder(new Sha256FileHasher(), new FileHeadSampler());
        var groups = await finder.FindAsync(scan.Files);

        Assert.Empty(groups); // 文件名相同、体积相同，但内容不同——一个组都不许给
    }

    [Fact]
    public async Task 头部抽样淘汰明显不同的文件()
    {
        Cleanup();
        var a = new byte[65536]; new Random(7).NextBytes(a);
        var b = new byte[65536]; new Random(8).NextBytes(b);
        WriteAllBytesDir("大文件甲.bin", a);
        WriteAllBytesDir("大文件乙.bin", b);

        var scanner = new PortableDiskScanner(new Sha256FileHasher());
        var scan = await scanner.ScanAsync(new[] { Root }, new ScanOptions { ComputeHashes = true });
        var finder = new DuplicateFileFinder(new Sha256FileHasher(), new FileHeadSampler());
        var groups = await finder.FindAsync(scan.Files);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task 三份相同文件只留一份可释放两份()
    {
        Cleanup();
        var data = new byte[1024];
        WriteAllBytesDir("one.dat", data);
        WriteAllBytesDir("two.dat", data);
        WriteAllBytesDir("three.dat", data);

        var scanner = new PortableDiskScanner(new Sha256FileHasher());
        var scan = await scanner.ScanAsync(new[] { Root }, new ScanOptions { ComputeHashes = true });
        var finder = new DuplicateFileFinder(new Sha256FileHasher(), new FileHeadSampler());
        var groups = await finder.FindAsync(scan.Files);

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Files.Count);
        Assert.Equal(2048, group.ReclaimableBytes);
    }

    [Fact]
    public async Task 取消后不留半成品()
    {
        Cleanup();
        var data = new byte[4096];
        WriteAllBytesDir("x1.bin", data);
        WriteAllBytesDir("x2.bin", data);

        var scanner = new PortableDiskScanner(new Sha256FileHasher());
        var scan = await scanner.ScanAsync(new[] { Root }, new ScanOptions { ComputeHashes = true });
        var finder = new DuplicateFileFinder(new Sha256FileHasher(), new FileHeadSampler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => finder.FindAsync(scan.Files, options: null, progress: null, cts.Token));
    }
}

/// <summary>垃圾清理规则引擎：只产出候选、白名单优先、绝不自动删除。</summary>
public class JunkRuleEngineTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "taokeeper-junk-tests");

    public JunkRuleEngineTests() => Directory.CreateDirectory(Root);

    private static void Cleanup()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private static void Write(string rel, byte[] data, DateTimeOffset? modified = null)
    {
        var full = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        if (modified is { } m) File.SetLastWriteTimeUtc(full, m.UtcDateTime);
    }

    private static Func<string, string?> VirtualEnv => key => null;

    [Fact]
    public async Task 命中模式与时间条件()
    {
        Cleanup();
        Write("cache/app_cache_001.db", new byte[100]);
        Write("logs/old.log", new byte[2048], DateTimeOffset.UtcNow.AddDays(-20));
        Write("logs/fresh.log", new byte[2048], DateTimeOffset.UtcNow); // 新文件不入选

        var rule = new JunkRule(
            "test-cache", "测试缓存", "测试", new[] { Root }, "*_cache_*.db", null, JunkRisk.Low);
        var ruleLog = new JunkRule(
            "test-log", "过期日志", "测试", new[] { Root }, "*.log", 14, JunkRisk.Low);

        var engine = new JunkRuleEngine(new[] { rule, ruleLog });
        var result = await engine.ScanAsync(VirtualEnv, new HashSet<string>(), rootsOverride: new[] { Root });

        var paths = result.Candidates.Select(c => c.FullPath).ToList();
        Assert.Contains(Path.Combine(Root, "cache", "app_cache_001.db"), paths);
        Assert.Contains(Path.Combine(Root, "logs", "old.log"), paths);
        Assert.DoesNotContain(Path.Combine(Root, "logs", "fresh.log"), paths);
        Assert.True(result.TotalBytes > 0);
        // 检查文件仍然在——引擎只读，绝不删除
        Assert.True(File.Exists(Path.Combine(Root, "cache", "app_cache_001.db")));
    }

    [Fact]
    public async Task 白名单整条排除()
    {
        Cleanup();
        Write("keep/keep_cache_special.db", new byte[10]);
        Write("free/free_cache_normal.db", new byte[10]);

        var rule = new JunkRule("t", "t", "t", new[] { Root }, "*_cache_*.db", null, JunkRisk.Low);
        var engine = new JunkRuleEngine(new[] { rule });
        var result = await engine.ScanAsync(
            VirtualEnv, new HashSet<string> { Path.Combine(Root, "keep") },
            rootsOverride: new[] { Root });

        var paths = result.Candidates.Select(c => c.FullPath).ToList();
        Assert.Contains(Path.Combine(Root, "free", "free_cache_normal.db"), paths);
        Assert.DoesNotContain(paths, p => p.StartsWith(Path.Combine(Root, "keep"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task 花括号扩展模式命中多种后缀()
    {
        Cleanup();
        Write("dl/setup.msi", new byte[10]);
        Write("dl/installer.exe", new byte[10]);
        Write("dl/readme.txt", new byte[10]);

        var rule = new JunkRule("t", "t", "t", new[] { Root }, "*.{msi,exe}", null, JunkRisk.Medium);
        var engine = new JunkRuleEngine(new[] { rule });
        var result = await engine.ScanAsync(VirtualEnv, new HashSet<string>(), rootsOverride: new[] { Root });

        var paths = result.Candidates.Select(c => Path.GetFileName(c.FullPath)).ToList();
        Assert.Contains("setup.msi", paths);
        Assert.Contains("installer.exe", paths);
        Assert.DoesNotContain("readme.txt", paths);
    }

    [Fact]
    public async Task 环境变量展开失败时规则跳过不炸()
    {
        Cleanup();
        Write("plain.tmp", new byte[5]);
        var rules = new List<JunkRule>
        {
            new("real", "真实目录", "t", new[] { Root }, "*", null, JunkRisk.Low),
            new("phantom", "不存在的目录", "t", new[] { "%NOT_A_REAL_VAR%\\Temp" }, "*", null, JunkRisk.Low),
        };
        var engine = new JunkRuleEngine(rules);
        var result = await engine.ScanAsync(VirtualEnv, new HashSet<string>());

        var paths = result.Candidates.Select(c => c.FullPath).ToList();
        Assert.Contains(Path.Combine(Root, "plain.tmp"), paths);
        Assert.All(paths, p => Assert.DoesNotContain("NOT_A_REAL_VAR", p));
    }
}
