using System;
using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>弹窗规则引擎：白名单优先、只做文本判定、全程留痕可回溯，绝不触碰真实窗口。</summary>
public class PopupRuleEngineTests
{
    private static readonly PopupEvent AdPopup = new("promoapp", "限时优惠不要错过", "PromoWindow");
    private static readonly PopupEvent NormalPopup = new("explorer", "文件夹", "CabinetWClass");

    private static IEnumerable<PopupWhitelistEntry> Whitelist => new List<PopupWhitelistEntry>
    {
        new("wl-1", PopupMatchKind.ProcessName, "explorer", Note: "系统进程一律放行"),
    };

    [Fact]
    public void 命中规则被拦截并留痕()
    {
        var engine = new PopupRuleEngine(new[]
        {
            new PopupRule("r1", "推广弹窗", PopupMatchKind.WindowTitlePattern, "*优惠*"),
        });

        var record = engine.Evaluate(AdPopup, Whitelist);
        Assert.Equal(PopupAction.Blocked, record.Action);
        Assert.Equal("r1", record.RuleId);
        Assert.Null(record.WhitelistId);
        Assert.Equal("promoapp", record.Popup.ProcessName);
        Assert.True(record.OccurredUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void 白名单优先于规则_放行留痕()
    {
        var engine = new PopupRuleEngine(new[]
        {
            new PopupRule("r1", "标题拦截", PopupMatchKind.WindowTitlePattern, "*"),
        });

        var record = engine.Evaluate(NormalPopup, Whitelist);
        Assert.Equal(PopupAction.Whitelisted, record.Action);
        Assert.Equal("wl-1", record.WhitelistId);
        Assert.Null(record.RuleId);
    }

    [Fact]
    public void 未命中默认放行()
    {
        var engine = new PopupRuleEngine(new[]
        {
            new PopupRule("r1", "推广弹窗", PopupMatchKind.WindowTitlePattern, "*优惠*"),
        });

        var record = engine.Evaluate(NormalPopup, Array.Empty<PopupWhitelistEntry>());
        Assert.Equal(PopupAction.Allowed, record.Action);
        Assert.Null(record.RuleId);
        Assert.Null(record.WhitelistId);
    }

    [Fact]
    public void 禁用规则不生效()
    {
        var engine = new PopupRuleEngine(new[]
        {
            new PopupRule("r1", "推广弹窗", PopupMatchKind.WindowTitlePattern, "*优惠*", Enabled: false),
        });

        var record = engine.Evaluate(AdPopup, Array.Empty<PopupWhitelistEntry>());
        Assert.Equal(PopupAction.Allowed, record.Action);
    }

    [Fact]
    public void 进程名匹配忽略大小写()
    {
        var engine = new PopupRuleEngine(new[]
        {
            new PopupRule("r1", "按进程名", PopupMatchKind.ProcessName, "PromoApp"),
        });

        var record = engine.Evaluate(AdPopup, Array.Empty<PopupWhitelistEntry>());
        Assert.Equal(PopupAction.Blocked, record.Action);
    }

    [Fact]
    public void 类名精确匹配()
    {
        var engine = new PopupRuleEngine(new[]
        {
            new PopupRule("r1", "按类名", PopupMatchKind.WindowClass, "PromoWindow"),
        });

        Assert.Equal(PopupAction.Blocked, engine.Evaluate(AdPopup, Array.Empty<PopupWhitelistEntry>()).Action);
        Assert.Equal(PopupAction.Allowed, engine.Evaluate(NormalPopup, Array.Empty<PopupWhitelistEntry>()).Action);
    }

    [Fact]
    public void 拦截记录支持回溯三要素()
    {
        var engine = new PopupRuleEngine(new[]
        {
            new PopupRule("r1", "推广弹窗", PopupMatchKind.WindowTitlePattern, "*优惠*"),
        });

        var hit = engine.Evaluate(AdPopup, Whitelist, note: "测试拦截");
        var pass = engine.Evaluate(NormalPopup, Whitelist, note: "白名单放行");

        // 可回溯：时间、命中对象、处理动作三要素齐备
        Assert.True(hit.OccurredUtc > DateTimeOffset.MinValue);
        Assert.Equal(PopupAction.Blocked, hit.Action);
        Assert.Equal("r1", hit.RuleId);
        Assert.Equal("白名单放行", pass.Note);
        Assert.Equal(PopupAction.Whitelisted, pass.Action);

        // 弹窗内容完整留痕，事后能还原是哪个窗口
        Assert.Equal("限时优惠不要错过", hit.Popup.WindowTitle);
        Assert.Equal("PromoWindow", hit.Popup.WindowClass);
    }
}
