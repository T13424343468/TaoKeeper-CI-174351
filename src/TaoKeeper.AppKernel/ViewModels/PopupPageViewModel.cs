using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.AppKernel.ViewModels;

/// <summary>弹窗管理页视图模型（UI 框架零依赖，WinUI3 壳直接绑定）。
/// 职责：规则清单与逐条开关、白名单维护、命中判定与拦截记录回溯。
/// 道系边界：本层只做文本匹配判定与留痕转述，不触碰真实窗口、不做窗口劫持；
/// 实际关闭/放行动作永远由 Windows 适配层在用户确认策略下执行。</summary>
public sealed class PopupPageViewModel
{
    /// <summary>拦截记录回溯展示上限（环形丢弃最旧）。</summary>
    public int RecentLimit { get; init; } = 50;

    private readonly List<PopupRule> _rules;
    private readonly List<PopupWhitelistEntry> _whitelist;
    private readonly List<PopupInterceptionRecord> _recent = new();

    /// <summary>配置变化钩子（宿主接线后落盘持久化；本层不做 IO）。</summary>
    public Action? ConfigChanged { get; set; }

    public PopupPageViewModel(
        IEnumerable<PopupRule>? rules = null,
        IEnumerable<PopupWhitelistEntry>? whitelist = null)
    {
        _rules = rules?.ToList() ?? new List<PopupRule>();
        _whitelist = whitelist?.ToList() ?? new List<PopupWhitelistEntry>();
    }

    /// <summary>把持久化的拦截记录回放进 VM（入参时间乱序均可，内部按新→旧重排并回填计数）。</summary>
    public void LoadRecords(IEnumerable<PopupInterceptionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _recent.Clear();
        _recent.AddRange(records.OrderByDescending(r => r.OccurredUtc));
        if (_recent.Count > RecentLimit)
            _recent.RemoveRange(RecentLimit, _recent.Count - RecentLimit);
        BlockedCount = _recent.Count(r => r.Action == PopupAction.Blocked);
        WhitelistedCount = _recent.Count(r => r.Action == PopupAction.Whitelisted);
        AllowedCount = _recent.Count(r => r.Action == PopupAction.Allowed);
    }

    /// <summary>只判不留痕：把一个真实窗口文本交给规则引擎，返回处置类别（检测清单展示用，不计数不入记录）。</summary>
    public PopupInterceptionRecord Classify(PopupEvent popup)
    {
        ArgumentNullException.ThrowIfNull(popup);
        return new PopupRuleEngine(_rules).Evaluate(popup, _whitelist);
    }

    /// <summary>当前规则清单（只读视图）。</summary>
    public IReadOnlyList<PopupRule> Rules => _rules;

    /// <summary>当前白名单（只读视图；命中即放行并留痕，优先于一切规则）。</summary>
    public IReadOnlyList<PopupWhitelistEntry> Whitelist => _whitelist;

    /// <summary>最近拦截记录（新→旧，环形保留 RecentLimit 条）。</summary>
    public IReadOnlyList<PopupInterceptionRecord> RecentRecords => _recent;

    /// <summary>累计计数：拦截 / 放行 / 白名单放行。</summary>
    public int BlockedCount { get; private set; }

    public int AllowedCount { get; private set; }

    public int WhitelistedCount { get; private set; }

    /// <summary>页面汇总文案（主显）。</summary>
    public string SummaryText =>
        $"规则 {EnabledRuleCount}/{_rules.Count} 启用 · 拦截 {BlockedCount} · 白名单放行 {WhitelistedCount} · 放行 {AllowedCount}";

    private int EnabledRuleCount => _rules.Count(r => r.Enabled);

    /// <summary>逐条开关一条规则（合规形态：每条规则独立可控）。</summary>
    public void SetRuleEnabled(string ruleId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        var index = _rules.FindIndex(r => r.Id == ruleId);
        if (index < 0)
            throw new KeyNotFoundException($"规则不存在：{ruleId}");
        _rules[index] = _rules[index] with { Enabled = enabled };
        ConfigChanged?.Invoke();
    }

    /// <summary>新增一条规则；Id 重复视为配置错误，报红不改静默。</summary>
    public void AddRule(PopupRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Id);
        if (_rules.Any(r => r.Id == rule.Id))
            throw new InvalidOperationException($"规则 Id 重复：{rule.Id}");
        _rules.Add(rule);
        ConfigChanged?.Invoke();
    }

    /// <summary>移除一条规则；不存在时报红。</summary>
    public void RemoveRule(string ruleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        if (_rules.RemoveAll(r => r.Id == ruleId) == 0)
            throw new KeyNotFoundException($"规则不存在：{ruleId}");
        ConfigChanged?.Invoke();
    }

    /// <summary>加入白名单；Id 重复报红。</summary>
    public void AddWhitelist(PopupWhitelistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Id);
        if (_whitelist.Any(w => w.Id == entry.Id))
            throw new InvalidOperationException($"白名单 Id 重复：{entry.Id}");
        _whitelist.Add(entry);
        ConfigChanged?.Invoke();
    }

    /// <summary>移除白名单条目；不存在时报红。</summary>
    public void RemoveWhitelist(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        if (_whitelist.RemoveAll(w => w.Id == entryId) == 0)
            throw new KeyNotFoundException($"白名单条目不存在：{entryId}");
        ConfigChanged?.Invoke();
    }

    /// <summary>判定一次弹窗并留痕（白名单优先 → 启用规则 → 默认放行）。</summary>
    public PopupInterceptionRecord Evaluate(PopupEvent popup, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(popup);
        var engine = new PopupRuleEngine(_rules);
        var record = engine.Evaluate(popup, _whitelist, note);
        _recent.Insert(0, record);
        if (_recent.Count > RecentLimit)
            _recent.RemoveRange(RecentLimit, _recent.Count - RecentLimit);

        switch (record.Action)
        {
            case PopupAction.Blocked: BlockedCount++; break;
            case PopupAction.Whitelisted: WhitelistedCount++; break;
            default: AllowedCount++; break;
        }
        ConfigChanged?.Invoke();
        return record;
    }

    /// <summary>一条拦截记录的回溯展示文案（时间 + 处置 + 命中来源 + 进程）。</summary>
    public static string Describe(PopupInterceptionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var source = record.WhitelistId is { } w ? $"白名单 {w}"
            : record.RuleId is { } r ? $"规则 {r}" : "未命中规则";
        return $"{record.OccurredUtc.ToLocalTime():HH:mm:ss} {ActionText(record.Action)} · {source} · {record.Popup.ProcessName}";
    }

    private static string ActionText(PopupAction action) => action switch
    {
        PopupAction.Blocked => "已拦截",
        PopupAction.Whitelisted => "白名单放行",
        _ => "已放行",
    };
}
