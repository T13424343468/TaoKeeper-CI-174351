using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// 文件粉碎引擎测试：红线 N1（快照未落盘绝不永久删除）逐用例验证；
/// 计划允许/拒绝（N6 关键目录逐段对齐）、粉碎全链（原文件消失、快照字节一致、释放空间实测）、
/// 快照失败原状保留、取消语义、到期清除与还原。
/// </summary>
public class FileShredderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taokeeper-shred-" + Guid.NewGuid().ToString("N"));
    private readonly string _snapRoot;

    public FileShredderTests()
    {
        _snapRoot = Path.Combine(_dir, "snapshots");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string NewFile(string relativeName, byte[] content)
    {
        var full = Path.Combine(_dir, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    private static byte[] Bytes(int size, byte fill = 0xAB)
    {
        var data = new byte[size];
        Array.Fill(data, fill);
        return data;
    }

    /// <summary>同步进度回调（Progress&lt;T&gt; 异步投递有竞态；取消语义需要同步时点）。</summary>
    private sealed class SyncProgress : IProgress<(int Index, string FullPath)>
    {
        private readonly Action<(int Index, string FullPath)> _action;
        public SyncProgress(Action<(int Index, string FullPath)> action) => _action = action;
        public void Report((int Index, string FullPath) value) => _action(value);
    }

    // ---------- BuildPlan ----------

    [Fact]
    public void 计划_存在的文件允许且体积如实()
    {
        var f = NewFile("a.txt", Bytes(1024));
        var engine = new FileShredder(_snapRoot);

        var plan = engine.BuildPlan(new[] { f });

        var item = Assert.Single(plan);
        Assert.True(item.Allowed);
        Assert.Equal(1024, item.SizeBytes);
        Assert.Equal(f, item.FullPath);
        Assert.Null(item.DeniedReason);
    }

    [Fact]
    public void 计划_目录与不存在文件拒绝且说明如实()
    {
        var dir = Path.Combine(_dir, "folder");
        Directory.CreateDirectory(dir);
        var missing = Path.Combine(_dir, "missing.txt");
        var engine = new FileShredder(_snapRoot);

        var plan = engine.BuildPlan(new[] { dir, missing });

        Assert.All(plan, i => Assert.False(i.Allowed));
        Assert.Contains("目录", plan[0].DeniedReason);
        Assert.Contains("不存在", plan[1].DeniedReason);
    }

    [Fact]
    public void 计划_关键目录内文件拒绝_N6()
    {
        var sys = Path.Combine(_dir, "sys");
        var inside = NewFile(Path.Combine("sys", "win.dll"), Bytes(10));
        var engine = new FileShredder(_snapRoot);

        var plan = engine.BuildPlan(new[] { inside }, new[] { sys });

        var item = Assert.Single(plan);
        Assert.False(item.Allowed);
        Assert.Contains("N6", item.DeniedReason);
    }

    [Fact]
    public void 计划_关键目录判定按段对齐_不按子串误判()
    {
        var sys = Path.Combine(_dir, "sys");
        var sibling = NewFile(Path.Combine("syskeep", "keep.txt"), Bytes(10));
        var engine = new FileShredder(_snapRoot);

        var plan = engine.BuildPlan(new[] { sibling }, new[] { sys });

        var item = Assert.Single(plan); // "syskeep" 不是 "sys" 之下，不命中关键目录
        Assert.True(item.Allowed);
    }

    // ---------- ShredAsync 全链 ----------

    [Fact]
    public async Task 粉碎_原文件消失_快照字节一致_释放空间实测()
    {
        var content = Bytes(4096);
        var f = NewFile("b.bin", content);
        var engine = new FileShredder(_snapRoot);
        var plan = engine.BuildPlan(new[] { f });

        var result = await engine.ShredAsync(plan);

        Assert.Equal(1, result.ShreddedCount);
        Assert.Equal(4096, result.FreedBytes);                      // 回执=实测，不编造
        Assert.False(File.Exists(f));                               // 原文件已消失
        var snapshot = Assert.Single(engine.ListSnapshots());
        Assert.Equal(f, snapshot.OriginalPath);
        Assert.Equal(4096, snapshot.SizeBytes);
        Assert.Equal(7, snapshot.RetentionDays);                    // 默认保留 7 天
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(_snapRoot, snapshot.SnapshotId, "payload.bin")));
    }

    [Fact]
    public async Task 粉碎_拒绝项不执行且错误如实_允许项照常()
    {
        var f = NewFile("c.bin", Bytes(128));
        var engine = new FileShredder(_snapRoot);
        var plan = engine.BuildPlan(new[] { f, Path.Combine(_dir, "missing.txt") });

        var result = await engine.ShredAsync(plan);

        Assert.Equal(1, result.ShreddedCount);
        Assert.False(File.Exists(f));
        Assert.False(plan[1].Allowed);
        Assert.False(result.Outcomes[1].Shredded);
        Assert.NotNull(result.Outcomes[1].Error);
    }

    [Fact]
    public async Task 粉碎_快照失败该条不碎_原状保留_N1()
    {
        var blocker = Path.Combine(_dir, "blocker");
        await File.WriteAllTextAsync(blocker, "占位文件：快照根下建子目录必败（ENOTDIR）");
        var f = NewFile("d.bin", Bytes(256));
        var engine = new FileShredder(blocker);                          // 快照根被文件占用=落盘必败
        var plan = engine.BuildPlan(new[] { f });

        var result = await engine.ShredAsync(plan);

        Assert.Equal(0, result.ShreddedCount);
        Assert.True(File.Exists(f));                                // 红线 N1：快照没落盘就绝不删
        Assert.Equal(Bytes(256), File.ReadAllBytes(f));
        Assert.All(result.Outcomes, o => Assert.False(o.Shredded));
        Assert.NotNull(result.Outcomes[0].Error);
    }

    [Fact]
    public async Task 粉碎_取消_已碎保持已碎_未处理原状()
    {
        var f1 = NewFile("e1.bin", Bytes(512));
        var f2 = NewFile("e2.bin", Bytes(512));
        var f3 = NewFile("e3.bin", Bytes(512));
        var engine = new FileShredder(_snapRoot);
        var plan = engine.BuildPlan(new[] { f1, f2, f3 });

        using var cts = new CancellationTokenSource();
        // 第 2 项开碎时取消（落在第 2 项快照写入中途）：第 1 项已碎（保持已碎），第 2、3 项未处理（保持原状）
        var progress = new SyncProgress(v => { if (v.Index == 1) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ShredAsync(plan, progress, cts.Token));

        Assert.False(File.Exists(f1));                              // 已碎：保持已碎（可从快照还原）
        Assert.True(File.Exists(f2));                               // 未处理：原状
        Assert.True(File.Exists(f3));                               // 未处理：原状
        Assert.Equal(Bytes(512), File.ReadAllBytes(f2));
        Assert.Equal(Bytes(512), File.ReadAllBytes(f3));
        Assert.Single(engine.ListSnapshots());
    }

    // ---------- 到期清除与还原 ----------

    [Fact]
    public async Task 到期清除_过期清_未过期留()
    {
        var f = NewFile("g.bin", Bytes(64));
        var engine = new FileShredder(_snapRoot);
        await engine.ShredAsync(engine.BuildPlan(new[] { f }));
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(0, engine.PurgeExpired(now));                  // 未到期：一份不清
        Assert.Single(engine.ListSnapshots());

        Assert.Equal(1, engine.PurgeExpired(now.AddDays(8)));       // 过保留期：物理清除
        Assert.Empty(engine.ListSnapshots());
        Assert.False(Directory.GetDirectories(_snapRoot).Any());    // payload 一并清除
    }

    [Fact]
    public async Task 还原_快照字节回拷原路径_快照保留可再还原()
    {
        var content = Bytes(300, 0x5A);
        var f = NewFile("h.bin", content);
        var engine = new FileShredder(_snapRoot);
        var plan = engine.BuildPlan(new[] { f });
        await engine.ShredAsync(plan);
        var snapshotId = engine.ListSnapshots().Single().SnapshotId;

        engine.Restore(snapshotId);

        Assert.True(File.Exists(f));
        Assert.Equal(content, File.ReadAllBytes(f));
        engine.Restore(snapshotId);                                 // 快照保留：可再次还原
        Assert.Equal(content, File.ReadAllBytes(f));
    }

    [Fact]
    public async Task 还原_快照不存在时如实报错_不假成功()
    {
        var engine = new FileShredder(_snapRoot);
        Assert.Throws<InvalidOperationException>(() => engine.Restore("no-such-snapshot"));

        var f = NewFile("i.bin", Bytes(16));
        await engine.ShredAsync(engine.BuildPlan(new[] { f }));
        Assert.Throws<InvalidOperationException>(() => engine.Restore("no-such-snapshot"));
    }

    [Fact]
    public async Task 清单_损坏快照跳过不假读()
    {
        var f = NewFile("j.bin", Bytes(16));
        var engine = new FileShredder(_snapRoot);
        await engine.ShredAsync(engine.BuildPlan(new[] { f }));

        var broken = Path.Combine(_snapRoot, "broken");
        Directory.CreateDirectory(broken);
        await File.WriteAllTextAsync(Path.Combine(broken, "meta.json"), "{不是json");

        var list = engine.ListSnapshots();
        var item = Assert.Single(list);                             // 损坏那份不进清单、不参与还原与到期判定
        Assert.NotEqual("broken", item.SnapshotId);
        Assert.ThrowsAny<Exception>(() => engine.Restore("broken"));
    }

    // ---------- 构造校验 ----------

    [Fact]
    public void 构造_参数越界如实拒绝()
    {
        Assert.Throws<ArgumentException>(() => new FileShredder(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileShredder(_snapRoot, retentionDays: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileShredder(_snapRoot, overwritePasses: 0));
    }
}
