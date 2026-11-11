using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Models;

namespace TaoKeeper.Core.Modules;

/// <summary>
/// 挂件开机自启的操作意图与启动项页的自洽过滤。
/// 关闭挂件 = 移除自启 + 结束进程，零后台残留；开启 = 登记自启，下次登录生效。
/// 自洽原则：管家自身组件（OwningProductId=TaoKeeper）在启动项页原样展示（透明），
/// 但绝不进入"建议禁用/清理"推荐——管家不清理管家自己，是否禁用由用户显式决定。
/// </summary>
public sealed class CleanWidgetPolicy
{
    public const string OwnProductId = "TaoKeeper";
    public const string WidgetAutostartItemId = "taokeeper-widget-autostart";

    /// <summary>挂件开关状态变化时，给出对启动项与进程的操作意图（适配层执行）。</summary>
    public WidgetBootAction ApplyToggle(bool widgetEnabled)
        => widgetEnabled
            ? new WidgetBootAction(WidgetBootIntent.RegisterAutostart, WidgetAutostartItemId,
                "挂件将在下次登录时随主程序启动；如需立即显示请在主界面点开。")
            : new WidgetBootAction(WidgetBootIntent.RemoveAutostartAndExit, WidgetAutostartItemId,
                "挂件已关闭：移除开机自启并退出，不留后台进程。");

    /// <summary>启动项页过滤：全部原样展示；"建议禁用"剔除管家自身与疑似同类工具。</summary>
    public (IReadOnlyList<StartupItemEntry> Items, IReadOnlyList<StartupItemEntry> RecommendedToDisable)
        FilterForStartupPage(IReadOnlyList<StartupItemEntry> allItems)
    {
        var recommended = allItems
            .Where(i => i.Risk == StartupRiskLevel.High || (i.Enabled && i.Risk == StartupRiskLevel.Medium))
            .Where(i => !IsOwnComponent(i))
            .Where(i => !LooksLikeKeeper(i))
            .ToList();
        return (allItems, recommended);
    }

    /// <summary>是否为管家自有组件（按归属产品字段判定）。</summary>
    public static bool IsOwnComponent(StartupItemEntry item)
        => string.Equals(item.OwningProductId, OwnProductId, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeKeeper(StartupItemEntry item)
        => item.Command?.Contains(OwnProductId, StringComparison.OrdinalIgnoreCase) == true
           || item.Publisher?.Contains(OwnProductId, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>挂件开关产生的启动项操作意图。</summary>
public sealed record WidgetBootAction(
    WidgetBootIntent Intent,
    string StartupItemId,
    string UserHint);

public enum WidgetBootIntent
{
    /// <summary>登记自启（开机/登录时由系统拉起，不驻留独立后台服务）。</summary>
    RegisterAutostart,
    /// <summary>移除自启并退出进程，零后台残留。</summary>
    RemoveAutostartAndExit,
}
