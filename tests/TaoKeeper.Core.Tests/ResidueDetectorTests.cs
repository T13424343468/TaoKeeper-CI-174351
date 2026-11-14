using System;
using System.Linq;
using TaoKeeper.Core.Modules.ResidueDetect;
using Xunit;

namespace TaoKeeper.Core.Tests;

public sealed class ResidueDetectorTests
{
    private sealed class FakeSource : IResidueNodeSource
    {
        public required IReadOnlyList<ResidueNode> Nodes { get; init; }
        public required IReadOnlyList<ResidueEdge> Edges { get; init; }
    }

    private static readonly DateTimeOffset T = new(2026, 11, 14, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A14_1_可达节点不进组()
    {
        // 根 → 卸载串 → 主程序文件：全链可达，零残留
        var src = new FakeSource
        {
            Nodes =
            [
                new(ResidueNodeType.RegistryKey, @"HKCU\Software\MyApp\Uninstall", Target: @"C:\Program Files\MyApp"),
                new(ResidueNodeType.File, @"C:\Program Files\MyApp\app.exe"),
            ],
            Edges = [new(@"HKCU\Software\MyApp\Uninstall", @"C:\Program Files\MyApp")],
        };
        var report = ResidueDetector.Analyze(src, [@"C:\Program Files\MyApp"]);
        Assert.Empty(report.CleanupCandidates);
        Assert.Empty(report.PendingConfirmation);
    }

    [Fact]
    public void A14_1_不可达且目标缺失_进清理候选()
    {
        // 快捷方式指向已删除文件（目标缺失）→ 残留候选
        var src = new FakeSource
        {
            Nodes =
            [
                new(ResidueNodeType.Shortcut, @"C:\Users\a\Desktop\ghost.lnk", Target: @"C:\Gone\app.exe"),
            ],
            Edges = [],
        };
        var report = ResidueDetector.Analyze(src, [@"C:\Program Files\MyApp"]);
        var candidate = Assert.Single(report.CleanupCandidates);
        Assert.Equal(@"C:\Users\a\Desktop\ghost.lnk", candidate.Path);
    }

    [Fact]
    public void A14_1_待确认组分离_列修改时间()
    {
        // 不可达但无缺失目标信息 → 待确认（人工判断：列路径+最近修改时间）
        var src = new FakeSource
        {
            Nodes =
            [
                new(ResidueNodeType.File, @"C:\Users\a\AppData\Local\orphan.dat", LastModifiedUtc: T),
            ],
            Edges = [],
        };
        var report = ResidueDetector.Analyze(src, [@"C:\Program Files\MyApp"]);
        Assert.Empty(report.CleanupCandidates);
        var pending = Assert.Single(report.PendingConfirmation);
        Assert.Equal(@"C:\Users\a\AppData\Local\orphan.dat", pending.Path);
        Assert.Equal(T, pending.LastModifiedUtc);
    }

    [Fact]
    public void C1_保守原则_根自身永不为残留()
    {
        var src = new FakeSource
        {
            Nodes = [new(ResidueNodeType.File, @"C:\Program Files\MyApp\keep.exe")],
            Edges = [],
        };
        var report = ResidueDetector.Analyze(src, [@"C:\Program Files\MyApp"]);
        Assert.Empty(report.CleanupCandidates);
        Assert.Empty(report.PendingConfirmation);
    }

    [Fact]
    public void A14_1_注册表键指向已删目录_进候选()
    {
        var src = new FakeSource
        {
            Nodes =
            [
                new(ResidueNodeType.RegistryKey, @"HKCU\Software\DeadApp", Target: @"C:\DeadApp"),
                new(ResidueNodeType.File, @"C:\Program Files\MyApp\app.exe"),
            ],
            Edges = [new(@"HKCU\Software\DeadApp", @"C:\DeadApp")],
        };
        var report = ResidueDetector.Analyze(src, [@"C:\Program Files\MyApp"]);
        var candidate = Assert.Single(report.CleanupCandidates);
        Assert.Equal(@"HKCU\Software\DeadApp", candidate.Path);
    }
}
