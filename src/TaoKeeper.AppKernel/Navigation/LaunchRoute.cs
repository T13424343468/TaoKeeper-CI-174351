using System;

namespace TaoKeeper.AppKernel.Navigation;

/// <summary>
/// 启动直达路由解析：--page &lt;pageId|中文标题&gt;（实效探针与排障直达页用）。
/// 只认导航目录已有路由；未识别或未传参一律返回 null（保持首页），不新增路由、不改原型顺序。
/// 重复传参按命令行惯例取最后一个生效。
/// </summary>
public static class LaunchRoute
{
    public static AppRoute? FromCommandLine(string[] args)
    {
        if (args is null) return null;
        AppRoute? result = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--page", StringComparison.Ordinal)) continue;
            var value = args[i + 1].Trim();
            if (value.Length == 0) continue;
            foreach (var item in AppRouteCatalog.Items)
            {
                if (string.Equals(item.PageId, value, StringComparison.OrdinalIgnoreCase) ||
                    item.Title == value)
                {
                    result = item.Route;
                    break;
                }
            }
        }
        return result;
    }
}
