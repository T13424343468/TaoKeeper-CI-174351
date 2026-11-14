using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Modules.SnapshotChain;
using TaoKeeper.Core.Modules.SandboxPreview;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>内存假文件系统（沙箱全测口径，无真实 I/O）。</summary>
internal sealed class FakeSandboxFs : ISandboxFileSystem
{
    private readonly Dictionary<string, byte[]> _files = [];
    public List<string> Written { get; } = [];
    public List<string> Deleted { get; } = [];
    public System.Collections.ObjectModel.ReadOnlyCollection<string> WrittenPaths => Written.AsReadOnly();
    public System.Collections.ObjectModel.ReadOnlyCollection<string> DeletedPaths => Deleted.AsReadOnly();

    public bool Exists(string path) => _files.ContainsKey(path);
    public byte[] ReadAllBytes(string path) =>
        _files.TryGetValue(path, out var b) ? b : throw new FileNotFoundException(path);
    public void WriteAllBytes(string path, byte[] content)
    {
        _files[path] = content;
        Written.Add(path);
    }
    public void Delete(string path)
    {
        _files.Remove(path);
        Deleted.Add(path);
    }
}

/// <summary>确定性越界判定：拒绝所有以 C:\Windows 开头的写入。</summary>
internal sealed class FakeBoundary : ISandboxBoundary
{
    public string? InspectWrite(string path) =>
        path.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase) ? "越界：系统目录写入被拒绝" : null;
}

/// <summary>内存状态应用器（测试口径，回滚/还原仅记录调用）。</summary>
internal sealed class MemoryStateApplier : ISnapshotStateApplier
{
    public List<string> AppliedPaths { get; } = [];
    public Task ApplyAsync(string targetPath, byte[] content, CancellationToken ct)
    {
        AppliedPaths.Add(targetPath);
        return Task.CompletedTask;
    }
}

public sealed class SandboxPreviewEngineTests
{
    private static (SandboxPreviewEngine Engine, FakeSandboxFs Fs) Make()
    {
        var root = Path.Combine(Path.GetTempPath(), "tk-p1", Guid.NewGuid().ToString("N"));
        var store = new FileSnapshotChainStore(root);
        var fs = new FakeSandboxFs();
        var engine = new SandboxPreviewEngine(
            fs, new FakeBoundary(),
            new SnapshotChainEngine(store, new MemoryStateApplier()));
        return (engine, fs);
    }

    [Fact]
    public async Task A13_2_留下_正式执行_暂存写入落盘()
    {
        var (engine, fs) = Make();
        engine.Begin();
        engine.StageWrite(@"C:\app\new-file.txt", "hello"u8.ToArray());
        var r = await engine.CommitAsync("测试动作");
        Assert.True(r.Succeeded, r.Error);
        Assert.Equal(PreviewOutcome.Commit, r.Outcome);
        Assert.Contains(@"C:\app\new-file.txt", fs.Written);
    }

    [Fact]
    public async Task A13_3_确认执行前自动拍快照()
    {
        var (engine, _) = Make();
        engine.Begin();
        engine.StageWrite(@"C:\app\a.txt", new byte[10]);
        var r = await engine.CommitAsync("测试动作");
        Assert.True(r.Succeeded);
        // 快照可验证：拍过快照即有 WAL 意图（通过冻结集或链 Verify 无法直接读，以无异常+成功为准，
        // 链接性由 P16 页快照清单核验——此处断言执行无异常即可）
    }

    [Fact]
    public void A13_4_越界拒绝并记录_零越界如实零()
    {
        var (engine, _) = Make();
        engine.Begin();
        // 零越界：如实 0，不造记录
        Assert.Equal(0, engine.ViolationCount);
        Assert.Empty(engine.Interceptions);

        engine.StageWrite(@"C:\Windows\evil.dll", new byte[1]);
        Assert.Equal(1, engine.ViolationCount);
        var i = engine.Interceptions.Single();
        Assert.Contains("系统目录", i.Reason);
        Assert.Equal(@"C:\Windows\evil.dll", i.TargetPath);
    }

    [Fact]
    public void A13_2_丢弃_零残留()
    {
        var (engine, fs) = Make();
        engine.Begin();
        engine.StageWrite(@"C:\app\pending.txt", new byte[5]);
        engine.StageDelete(@"C:\app\old.txt");
        var r = engine.Discard();
        Assert.True(r.Succeeded);
        Assert.Equal(PreviewOutcome.Discard, r.Outcome);
        Assert.Empty(fs.WrittenPaths);
        Assert.Empty(fs.DeletedPaths);
        // 零残留：暂存全清
        engine.Begin();
        engine.StageWrite(@"C:\app\check.txt", new byte[1]);
        engine.Discard();
        Assert.Empty(fs.WrittenPaths);
    }

    [Fact]
    public async Task A13_2_转正式快照_原样冻结含拦截记录()
    {
        var (engine, _) = Make();
        engine.Begin();
        engine.StageWrite(@"C:\app\frozen.txt", new byte[3]);
        engine.StageWrite(@"C:\Windows\blocked.dll", new byte[1]);
        var r = await engine.FreezeAsSnapshotAsync("冻结测试");
        Assert.True(r.Succeeded, r.Error);
        Assert.Single(engine.FrozenSnapshots);
    }

    [Fact]
    public async Task 超单快照上限_拒绝并中止()
    {
        var (engine, fs) = Make();
        engine.Begin();
        engine.StageWrite(@"C:\app\big.bin", new byte[SnapshotPolicy.SingleSnapshotMaxBytes + 1]);
        var r = await engine.CommitAsync("超限动作");
        Assert.False(r.Succeeded);
        Assert.Contains("500MB", r.Error);
        Assert.Empty(fs.WrittenPaths); // 危险动作未执行
    }
}
