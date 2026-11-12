using System;
using Microsoft.UI.Xaml;
using TaoKeeper.AppKernel.Settings;
using TaoKeeper.AppKernel.Theming;

namespace TaoKeeper.App.Theming;

/// <summary>
/// 主题换肤：道系三主题（水墨/天青/墨白）在 XAML 资源层的落点。
/// 三份主题字典（Themes/TaoTheme.*.xaml）色值与 AppKernel.Theming.ThemeTokens 同源
/// （取自原型 v19 的 :root / data-theme 块），改动须两端同步；
/// 运行期切换 = 整字典替换（MergedDictionaries 摘旧插新，全局即时生效）。
/// </summary>
public static class AppTheme
{
    /// <summary>按主题名换入对应资源字典；未知名按默认水墨（不抛异常打断启动）。</summary>
    public static void Apply(Application app, ThemeName name)
    {
        ArgumentNullException.ThrowIfNull(app);
        var source = name switch
        {
            ThemeName.Tianqing => "ms-appx:///Themes/TaoTheme.Tianqing.xaml",
            ThemeName.Mobai => "ms-appx:///Themes/TaoTheme.Mobai.xaml",
            _ => "ms-appx:///Themes/TaoTheme.Shuimo.xaml", // 水墨=默认
        };

        var dict = new ResourceDictionary { Source = new Uri(source) };
        var merged = app.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i].Source?.OriginalString.Contains("Themes/TaoTheme.") == true)
                merged.RemoveAt(i);
        }
        // 合并字典按"后者优先"解析：主题字典必须排在 XamlControlsResources 等基础字典
        // 之后（末尾），否则换肤后自研色值会被基础资源覆盖。
        merged.Insert(merged.Count, dict);
    }

    /// <summary>从设置文件读主题（损坏/缺失回退默认水墨，设置层已有回退语义）。</summary>
    public static ThemeName LoadThemeFromSettings()
    {
        try
        {
            var folder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            var store = new AppSettingsStore(System.IO.Path.Combine(folder, "settings.json"));
            return store.Load().Theme;
        }
        catch (InvalidOperationException)
        {
            return ThemeName.Shuimo; // 应用数据未就绪（如设计期宿主）按默认主题
        }
        catch (UnauthorizedAccessException)
        {
            return ThemeName.Shuimo; // 本地数据不可写时退默认，不让主题挡住启动
        }
    }
}
