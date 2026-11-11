using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoKeeper.Core.Modules;

/// <summary>匹配维度：按进程名 / 窗口标题通配 / 窗口类名匹配。</summary>
public enum PopupMatchKind { ProcessName, WindowTitlePattern, WindowClass }

/// <summary>一条弹窗拦截规则。</summary>
public sealed record PopupRule(
    string Id,
    string Title,
    PopupMatchKind MatchKind,
    /// <summary>匹配值：进程名精确比较（忽略大小写）；标题/类名支持 * 与 ? 通配。</summary>
    string Pattern,
    bool Enabled = true);

/// <summary>用户弹窗白名单：命中的弹窗放行并留痕，优先于一切规则。</summary>
public sealed record PopupWhitelistEntry(
    string Id,
    /// <summary>白名单维度与规则一致，通常按进程名放行整个应用。</summary>
    PopupMatchKind MatchKind,
    string Pattern,
    string? Note = null);

/// <summary>一次待判定的弹窗事件快照（不含句柄，仅判定所需文本）。</summary>
public sealed record PopupEvent(
    string ProcessName,
    string WindowTitle,
    string WindowClass);

/// <summary>对命中弹窗的处理动作。</summary>
public enum PopupAction { Blocked, Allowed, Whitelisted }

/// <summary>一条拦截/放行记录，支持回溯：什么时间、命中哪条、怎么处理的。</summary>
public sealed record PopupInterceptionRecord(
    DateTimeOffset OccurredUtc,
    /// <summary>命中的规则标识；白名单放行时为 null，取 WhitelistId。</summary>
    string? RuleId,
    /// <summary>白名单放行时的白名单条目标识；规则拦截时为 null。</summary>
    string? WhitelistId,
    PopupEvent Popup,
    PopupAction Action,
    /// <summary>用户动作备注（如"手动放行一次"），可为空。</summary>
    string? Note = null);

/// <summary>
/// 弹窗管理规则引擎（模块 M7，可移植逻辑层）。
/// 只做文本匹配判定与留痕，不触碰真实窗口句柄、不做任何注入或窗口劫持——
/// 实际的窗口关闭/放行动作由 Windows 适配层在用户确认策略下执行。
/// </summary>
public sealed class PopupRuleEngine
{
    private readonly IReadOnlyList<PopupRule> _rules;

    public PopupRuleEngine(IReadOnlyList<PopupRule>? rules = null)
        => _rules = rules ?? new List<PopupRule>();

    /// <summary>判定一次弹窗：白名单优先 → 启用规则匹配 → 未命中默认放行。全程留痕。</summary>
    public PopupInterceptionRecord Evaluate(
        PopupEvent popup,
        IEnumerable<PopupWhitelistEntry> whitelist,
        string? note = null)
    {
        foreach (var w in whitelist)
        {
            if (!string.Equals(w.MatchKind.ToString(), w.MatchKind.ToString(), StringComparison.Ordinal))
                continue; // 防御：不应出现
            if (Matches(popup, w.MatchKind, w.Pattern))
            {
                return new PopupInterceptionRecord(
                    DateTimeOffset.UtcNow, RuleId: null, WhitelistId: w.Id, popup, PopupAction.Whitelisted, note);
            }
        }

        foreach (var rule in _rules)
        {
            if (!rule.Enabled) continue;
            if (Matches(popup, rule.MatchKind, rule.Pattern))
            {
                return new PopupInterceptionRecord(
                    DateTimeOffset.UtcNow, RuleId: rule.Id, WhitelistId: null, popup, PopupAction.Blocked, note);
            }
        }

        return new PopupInterceptionRecord(
            DateTimeOffset.UtcNow, RuleId: null, WhitelistId: null, popup, PopupAction.Allowed, note);
    }

    /// <summary>通配匹配（* 与 ?），进程名忽略大小写精确比较。</summary>
    private static bool Matches(PopupEvent popup, PopupMatchKind kind, string pattern) => kind switch
    {
        PopupMatchKind.ProcessName => string.Equals(popup.ProcessName, pattern, StringComparison.OrdinalIgnoreCase),
        PopupMatchKind.WindowTitlePattern => WildcardMatch(popup.WindowTitle, pattern),
        PopupMatchKind.WindowClass => string.Equals(popup.WindowClass, pattern, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool WildcardMatch(string text, string pattern)
    {
        int t = 0, p = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { t++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { starP = p++; starT = t; }
            else if (starP >= 0) { p = starP + 1; t = ++starT; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
