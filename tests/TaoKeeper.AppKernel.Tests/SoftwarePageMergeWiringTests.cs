using System.Collections.Generic;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.AppKernel.Tests;

/// <summary>W7 软件管家双源合并注入测试：用户侧零来源外显、审计留原值、总数同步刷新。</summary>
public class SoftwarePageMergeWiringTests
{
    [Fact]
    public void ApplyMergedInstalled_StoresUserSideListAndAudit()
    {
        var vm = new SoftwarePageViewModel();
        var entries = new List<SoftwareEntry>
        {
            new("a", "软件甲", "Contoso", "1.0", null, SoftwareSourceKind.WingetCommunity, true, "winget 社区源"),
            new("b", "软件乙", null, "1.0", null, SoftwareSourceKind.OfficialPageManual, null, "注册表登记"),
        };
        var audit = new List<SoftwareMergeAudit> { new("软件甲", "Contoso", "双源同软件，字段择优", "winget 社区源", "注册表登记") };

        var result = vm.ApplyMergedInstalled(entries, audit);

        Assert.Equal(2, vm.MergedInstalled!.Count);
        Assert.All(vm.MergedInstalled, static e => Assert.Null(e.SourceDisplay)); // 用户侧零外显
        Assert.Single(vm.MergeAudit);
        Assert.Equal("winget 社区源", vm.MergeAudit[0].WingetSourceDisplay); // 审计留原值
        Assert.Equal(2, vm.InstalledCount); // 总数同步刷新
        Assert.Equal(vm, result); // 链式返回
    }

    [Fact]
    public void ApplyMergedInstalled_DefaultAuditEmpty()
    {
        var vm = new SoftwarePageViewModel();
        vm.ApplyMergedInstalled(new List<SoftwareEntry> { new("a", "x", null, null, null, SoftwareSourceKind.OfficialPageManual) });
        Assert.Empty(vm.MergeAudit); // 不传审计按空处理，不假记录
    }
}
