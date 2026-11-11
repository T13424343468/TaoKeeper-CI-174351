namespace TaoKeeper.AppKernel.Theming;

/// <summary>三主题：水墨（默认）、天青、墨白。命名与开关文案严格取自原型 v19 设置页。</summary>
public enum ThemeName
{
    Shuimo = 0,   // 水墨（默认）
    Tianqing,     // 天青
    Mobai,        // 墨白
}

/// <summary>
/// 主题令牌：值与原型 v19 CSS 变量逐字一致（:root 即水墨；tianqing/mobai 覆盖段）。
/// 墨白主题的 body 底色（#0d0e0f）与窗体描边另行暴露。
/// 所有主题、所有元素须有陶瓷釉面质感（道系长期要求）——釉面令牌随主题给出。
/// </summary>
public sealed record ThemeTokens(
    string Id,
    string DisplayName,
    string Paper, string Card, string Line, string LineSoft,
    string Ink, string Ink2, string Ink3,
    string Azure, string AzureDeep, string AzureWash,
    string Cinnabar, string CinnabarWash,
    string Bamboo, string BambooWash,
    string Gold,
    string Shadow, string Radius,
    string Glaze, string GlazeSoft, string GlazeTop,
    string HeroSky, string HeroH1, string HeroH2, string HeroH3, string HeroSun,
    string? BodyBackground = null,
    string? Strong = null)
{
    public static readonly ThemeTokens Shuimo = new(
        Id: "shuimo", DisplayName: "水墨",
        Paper: "#f6f5f1", Card: "rgba(255,255,255,.74)", Line: "#b0ada2", LineSoft: "#eae8e2",
        Ink: "#2b2a26", Ink2: "#6b6a65", Ink3: "#86837c",
        Azure: "#8a8878", AzureDeep: "#5f5e50", AzureWash: "#f0efe9",
        Cinnabar: "#b5443a", CinnabarWash: "#f9eeec",
        Bamboo: "#6a8f6b", BambooWash: "#eef3ee",
        Gold: "#a68a4e",
        Shadow: "0 10px 34px rgba(58,54,44,.10), 0 2px 8px rgba(58,54,44,.06)",
        Radius: "14px",
        Glaze: "rgba(255,255,255,.95)", GlazeSoft: "rgba(255,255,255,.78)", GlazeTop: "rgba(255,255,255,.9)",
        HeroSky: "#f2f1ec", HeroH1: "#d2cfc6", HeroH2: "#a5a298", HeroH3: "#6f6d64", HeroSun: "#b5443a");

    public static readonly ThemeTokens Tianqing = Shuimo with
    {
        Id = "tianqing", DisplayName = "天青",
        Paper = "#f0f7fb", Card = "rgba(247,252,254,.86)", Line = "#7fa8bd", LineSoft = "#e4eef6",
        Azure = "#4e8296", AzureDeep = "#3a6273", AzureWash = "#e5f0f6",
        HeroSky = "#e9f4fb", HeroH1 = "#d8e9f4", HeroH2 = "#b6d5e8", HeroH3 = "#8fbcdc", HeroSun = "#e2b45c",
    };

    public static readonly ThemeTokens Mobai = new(
        Id: "mobai", DisplayName: "墨白",
        Paper: "#191b1c", Card: "#242729", Line: "#535b60", LineSoft: "#2e3335",
        Ink: "#eceff0", Ink2: "#a7b0b0", Ink3: "#7e8788",
        Azure: "#7fb2c2", AzureDeep: "#93c2d0", AzureWash: "#2e3a3e",
        Cinnabar: "#cf6b5f", CinnabarWash: "#3a2c29",
        Bamboo: "#8ab48a", BambooWash: "#2c352c",
        Gold: "#c2a869",
        Shadow: "0 10px 34px rgba(0,0,0,.5),0 2px 8px rgba(0,0,0,.4)",
        Radius: "14px",
        Glaze: "rgba(255,255,255,.22)", GlazeSoft: "rgba(255,255,255,.12)", GlazeTop: "rgba(255,255,255,.12)",
        HeroSky: "#22262a", HeroH1: "#2f3538", HeroH2: "#3c4448", HeroH3: "#4c565a", HeroSun: "#d9d4c4",
        BodyBackground: "#0d0e0f",
        Strong: "#4e7d8c");

    public static ThemeTokens Of(ThemeName name) => name switch
    {
        ThemeName.Shuimo => Shuimo,
        ThemeName.Tianqing => Tianqing,
        ThemeName.Mobai => Mobai,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>主题三选一在设置页全局即时生效；默认水墨。</summary>
    public static ThemeTokens Default => Shuimo;
}
