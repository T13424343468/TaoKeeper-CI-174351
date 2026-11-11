using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Models;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>
/// 启动项页视图模型（UI 框架零依赖，WinUI3 壳直接绑定）：
/// 只读展示骨架——清单、来源/风险/状态文案与摘要。红线：本层零写动作，
/// 不提供启用/禁用调用；改系统的动作统一走性能加速页的确认闸门，本页只看不动。
/// </summary>
public sealed class StartupPageViewModel
{
    /// <summary>启动项清单（只读；未扫描为空清单，界面显示未测态）。</summary>
    public IReadOnlyList<StartupItemEntry> Items { get; private set; } = Array.Empty<StartupItemEntry>();

    /// <summary>页面摘要行（未扫描态固定文案，不假数据）。</summary>
    public string SummaryText { get; private set; } = "尚未扫描启动项";

    /// <summary>是否已有清单（界面切换空态用）。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>
    /// 只读扫描：仅调用适配器列清单（ListStartupItemsAsync），不触发任何写动作。
    /// </summary>
    public async Task<StartupPageViewModel> LoadAsync(
        IStartupControlAdapter adapter, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        Items = await adapter.ListStartupItemsAsync(cancellation).ConfigureAwait(false);

        // 摘要按真实清单统计；扫描不到任何项也如实说，不编「一切正常」之外的含义
        var enabled = Items.Count(i => i.Enabled);
        var high = Items.Count(i => i.Risk == StartupRiskLevel.High);
        SummaryText = Items.Count == 0
            ? "扫描完成：没有发现开机启动项"
            : $"共 {Items.Count} 项：启用中 {enabled}，已禁用 {Items.Count - enabled}"
              + (high > 0 ? $"，高风险 {high} 项" : string.Empty);
        return this;
    }

    /// <summary>来源文案（与注册表 Run 键/启动文件夹等真实来源一一对应）。</summary>
    public static string SourceText(StartupItemSource source) => source switch
    {
        StartupItemSource.RegistryRun => "注册表 Run 键",
        StartupItemSource.StartupFolder => "启动文件夹",
        StartupItemSource.ScheduledTask => "计划任务",
        _ => "系统服务",
    };

    /// <summary>风险文案（未确认发布者等未知状态如实呈现，不替用户下结论）。</summary>
    public static string RiskText(StartupItemEntry item) => item.Risk switch
    {
        StartupRiskLevel.High => "高风险",
        StartupRiskLevel.Medium => "未确认发布者",
        _ => "低风险",
    };

    /// <summary>状态文案：启用中/已禁用（枚举即事实，无未测分支）。</summary>
    public static string StateText(StartupItemEntry item) => item.Enabled ? "启用中" : "已禁用";

    /// <summary>明细行文案：来源 + 发布者 + 命令行（缺失字段如实省略）。</summary>
    public static string DetailText(StartupItemEntry item)
    {
        var parts = new[]
        {
            SourceText(item.Source),
            item.Publisher is { } publisher ? $"发布者 {publisher}" : null,
            item.Command is { } command ? command : null,
        };
        return string.Join(" · ", parts.Where(p => p is not null));
    }

    /// <summary>风险色（高风险=朱砂、未确认=金、低风险=竹青），随主题即时取值。</summary>
    public string RiskColorOf(StartupItemEntry item, ThemeTokens tokens) => item.Risk switch
    {
        StartupRiskLevel.High => tokens.Cinnabar,
        StartupRiskLevel.Medium => tokens.Gold,
        _ => tokens.Bamboo,
    };
}
