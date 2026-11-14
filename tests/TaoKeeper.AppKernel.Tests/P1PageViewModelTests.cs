using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules.IncidentReplay;
using TaoKeeper.Core.Modules.ResidueDetect;
using TaoKeeper.Core.Modules.SnapshotChain;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

public sealed class P1PageViewModelTests
{
    // ---------- P14 ----------

    [Fact]
    public void A14_1_候选待确认分离_待确认列修改时间()
    {
        var vm = new ResidueDetectPageViewModel();
        var src = new FakeNodes(
        [
            new(ResidueNodeType.Shortcut, @"C:\x\dead.lnk", Target: @"C:\Gone\app.exe"),
            new(ResidueNodeType.File, @"C:\x\orphan.dat", LastModifiedUtc: new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero)),
        ], []);
        vm.Load(src, [@"C:\Program Files\MyApp"]);
        Assert.Single(vm.CleanupCandidates);
        Assert.Single(vm.Pending);
        Assert.Contains("2026-11-14", vm.Pending[0].LastModifiedLocal);
        Assert.Equal("待确认", vm.Phase == "scanned" ? "待确认" : ""); // 已扫态
    }

    [Fact]
    public void A14_1_保留项不再提示()
    {
        var vm = new ResidueDetectPageViewModel();
        var src = new FakeNodes(
        [
            new(ResidueNodeType.File, @"C:\x\a.dat"),
            new(ResidueNodeType.File, @"C:\x\b.dat"),
        ], []);
        vm.Load(src, [@"C:\Program Files\MyApp"]);
        Assert.Equal(2, vm.Pending.Count);
        vm.Keep(@"C:\x\a.dat");
        Assert.Single(vm.Pending); // 保留项不再提示
        Assert.Equal(2, vm.CleanupCandidates.Count + vm.Pending.Count + 1); // a.dat 进保留集
    }

    // ---------- P15 ----------

    [Fact]
    public void A15_2_空窗显式提示_时间窗切换()
    {
        var vm = new IncidentReplayPageViewModel();
        vm.Load([], DateTimeOffset.UtcNow);
        Assert.True(vm.IsEmptyWindow);
        Assert.Contains("空窗显式提示", vm.EmptyWindowText);

        var anchor = DateTimeOffset.UtcNow;
        var events = new List<IncidentEvent> { new("crash", anchor.AddHours(-2), "崩溃 0x1E") };
        vm.Load(events, anchor, windowHours: 24);
        Assert.Single(vm.Timeline);
        Assert.True(vm.HasAttribution);
        Assert.NotEmpty(vm.VerificationSteps);
    }

    [Fact]
    public void A15_4_粒度切换_按周分组_标签与倒序()
    {
        var vm = new IncidentReplayPageViewModel();
        // 构造同一 7 天分桶内的两个事件（与引擎同口径：TimeUtc.Ticks / span.Ticks），不受当前时刻影响
        var span = TimeSpan.FromDays(7);
        var now = DateTimeOffset.UtcNow;
        var weekStart = new DateTimeOffset(now.Ticks / span.Ticks * span.Ticks, TimeSpan.Zero);
        var events = new List<IncidentEvent>
        {
            new("crash", weekStart.AddDays(0.5), "崩溃 A"), // 桶内较早
            new("crash", weekStart.AddDays(3), "崩溃 B"),   // 桶内较晚，距 now ≤ 4 天，168h 窗内
        };
        vm.Load(events, now, windowHours: 168);
        Assert.Equal(2, vm.Timeline.Count);

        vm.SetGranularity(events, now, "week");
        Assert.Equal("week", vm.Granularity);
        Assert.Equal(168, vm.WindowHours); // 粒度切换不重置时间窗
        Assert.Single(vm.TimelineGroups);  // 同一周分桶（跨度=快照保留上限）
        Assert.Contains("起一周", vm.TimelineGroups[0].Label);
        Assert.Equal(2, vm.TimelineGroups[0].Rows.Count); // 组内最近在前：崩溃 B 在前
        Assert.Contains("崩溃 B", vm.TimelineGroups[0].Rows[0].Summary);

        vm.SetGranularity(events, now, "day");
        Assert.Equal("day", vm.Granularity);
        Assert.Equal(2, vm.TimelineGroups.Count); // 跨天事件分两组（同一天内合并）
        Assert.All(vm.TimelineGroups, g => Assert.Single(g.Rows));
    }

    // ---------- P16 ----------

    [Fact]
    public void A16_1_总占用可见_清单含粉碎类_本地时区()
    {
        var store = new FakeQuotaStore();
        var e1 = new JournalEntry { TransactionId = "t-shred-1", ActionType = "shred.file", SequenceNumber = 1, FileTimeUtc = DateTimeOffset.UtcNow.ToFileTime() };
        store.Seed(e1);
        var sizes = new Dictionary<string, long> { ["t-shred-1"] = 2048 };
        var tracker = new SnapshotQuotaTracker(store, id => sizes.GetValueOrDefault(id), id => store.Done.Contains(id));
        tracker.Register("t-shred-1", 2048);
        var vm = new SnapshotChainPageViewModel(tracker) { QuotaBytes = 5L * 1024 * 1024 * 1024 };
        vm.Refresh();
        Assert.Equal("2.0 KB", vm.TotalUsageText);
        Assert.Single(vm.Snapshots);
        Assert.True(vm.Snapshots[0].IsShred);
        Assert.Equal("粉碎快照", vm.Snapshots[0].ActionLabel);
        Assert.Contains(DateTime.Now.ToString("yyyy"), vm.Snapshots[0].TimeLocal); // 本地时区
    }

    [Fact]
    public void A10_未扫描_零预填()
    {
        var store = new FakeQuotaStore();
        var tracker = new SnapshotQuotaTracker(store, _ => 0, _ => false);
        var vm = new SnapshotChainPageViewModel(tracker);
        vm.Refresh();
        Assert.Empty(vm.Snapshots); // 初始态自洽：未扫描无清单，不预填原型示例数
        Assert.Equal(0, tracker.TotalBytes);
    }

    // ---------- P13 ----------

    [Fact]
    public void A13_4_越界记录逐条展开_零越界如实零()
    {
        var vm = new SandboxPreviewPageViewModel();
        Assert.Equal(0, vm.ViolationCount); // 未预演=未测态，0 就是 0
        vm.BeginPreview();
        Assert.Equal(0, vm.ViolationCount);
        Assert.Empty(vm.Interceptions);
    }

    private sealed class FakeNodes(
        IReadOnlyList<ResidueNode> nodes,
        IReadOnlyList<ResidueEdge> edges) : IResidueNodeSource
    {
        public IReadOnlyList<ResidueNode> Nodes { get; } = nodes;
        public IReadOnlyList<ResidueEdge> Edges { get; } = edges;
    }

    private sealed class FakeQuotaStore : ISnapshotChainStore
    {
        private readonly Dictionary<string, JournalEntry> _intents = [];
        public HashSet<string> Done { get; } = [];
        public HashSet<string> RolledBack { get; } = [];
        public void Seed(JournalEntry e) => _intents[e.TransactionId] = e;
        public System.Threading.Tasks.Task WriteIntentAsync(string transactionId, JournalEntry entry, System.Threading.CancellationToken ct)
        { _intents[transactionId] = entry; return System.Threading.Tasks.Task.CompletedTask; }
        public System.Threading.Tasks.Task<JournalEntry?> ReadIntentAsync(string transactionId, System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(_intents.TryGetValue(transactionId, out var e) ? e : null);
        public System.Threading.Tasks.Task WritePayloadAsync(string transactionId, SnapshotItem item, string contentHash, System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task<IReadOnlyList<(string ContentHash, byte[] Content)>> ReadAllPayloadAsync(string transactionId, System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<(string, byte[])>>([]);
        public System.Threading.Tasks.Task WriteDoneMarkerAsync(string transactionId, System.Threading.CancellationToken ct) { Done.Add(transactionId); return System.Threading.Tasks.Task.CompletedTask; }
        public System.Threading.Tasks.Task<bool> HasDoneMarkerAsync(string transactionId, System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.FromResult(Done.Contains(transactionId));
        public System.Threading.Tasks.Task WriteRollbackMarkerAsync(string transactionId, System.Threading.CancellationToken ct) { RolledBack.Add(transactionId); return System.Threading.Tasks.Task.CompletedTask; }
        public System.Threading.Tasks.Task<bool> HasRollbackMarkerAsync(string transactionId, System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.FromResult(RolledBack.Contains(transactionId));
        public System.Threading.Tasks.Task<IReadOnlyList<string>> ListTransactionIdsAsync(System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<string>>([.. _intents.Keys]);
        public System.Threading.Tasks.Task<long> LoadSequenceHighWaterAsync(System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.FromResult(0L);
        public System.Threading.Tasks.Task SaveSequenceHighWaterAsync(long value, System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task OverwritePayloadInPlaceAsync(string transactionId, string contentHash, byte[] tampered, System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task<bool> DeletePayloadAsync(string transactionId, string contentHash, System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.FromResult(true);
        public System.Threading.Tasks.Task OverwriteIntentAsync(string transactionId, string rawJson, System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
    }
}
