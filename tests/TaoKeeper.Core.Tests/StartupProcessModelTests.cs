using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Models;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>启动项/进程数据模型：条目完整、快照统计正确、动作回执文案贴合界面。</summary>
public class StartupProcessModelTests
{
    private static readonly IReadOnlyList<StartupItemEntry> Items = new List<StartupItemEntry>
    {
        new("s1", "OneDrive", StartupItemSource.RegistryRun, "Microsoft Corporation", Enabled: true, StartupRiskLevel.Low),
        new("s2", "某某助手", StartupItemSource.StartupFolder, null, Enabled: false, StartupRiskLevel.High,
            RiskHint: "常见捆绑推广启动项，建议保持禁用", Command: "C:\\Users\\x\\AppData\\Roaming\\assistant.exe"),
        new("s3", "更新调度", StartupItemSource.ScheduledTask, "未知发布者", Enabled: true, StartupRiskLevel.Medium),
    };

    private static readonly IReadOnlyList<ProcessEntry> Processes = new List<ProcessEntry>
    {
        new(100, "chrome", "Google LLC", 12.5, 2L * 1024 * 1024 * 1024, IsSigned: true),
        new(101, "promo", null, 3.0, 512L * 1024 * 1024, IsSigned: false),
        new(102, "sysmon", null, 0.5, 64L * 1024 * 1024, IsSigned: null),
    };

    [Fact]
    public void 快照禁用项计数正确()
    {
        var snapshot = new StartupProcessSnapshot(DateTimeOffset.UtcNow, Items, Processes);
        Assert.Equal(1, snapshot.DisabledCount);
    }

    [Fact]
    public void 启动项字段完整_风险提示可展示()
    {
        var risky = Items.Single(i => i.Id == "s2");
        Assert.False(risky.Enabled);
        Assert.Equal(StartupItemSource.StartupFolder, risky.Source);
        Assert.Equal("常见捆绑推广启动项，建议保持禁用", risky.RiskHint);
        Assert.Contains("assistant.exe", risky.Command);
        Assert.Null(risky.Publisher); // 未知发布者保留 null，不编造
    }

    [Fact]
    public void 进程签名占位三种状态()
    {
        Assert.True(Processes[0].IsSigned);
        Assert.False(Processes[1].IsSigned);
        Assert.Null(Processes[2].IsSigned); // 未核验，不误报
    }

    [Fact]
    public void 动作回执文案()
    {
        var okDisable = new StartupToggleResult("s1", RequestedEnabled: false, Success: true);
        Assert.Equal("已禁用，下次开机生效", okDisable.DisplayText);

        var okEnable = new StartupToggleResult("s1", RequestedEnabled: true, Success: true);
        Assert.Equal("已启用，下次开机生效", okEnable.DisplayText);

        var fail = new StartupToggleResult("s2", RequestedEnabled: true, Success: false, Detail: "权限不足，请以管理员身份重试");
        Assert.Equal("权限不足，请以管理员身份重试", fail.DisplayText);

        var failNoDetail = new StartupToggleResult("s2", RequestedEnabled: false, Success: false);
        Assert.Equal("操作未完成，请重试", failNoDetail.DisplayText);
    }
}
