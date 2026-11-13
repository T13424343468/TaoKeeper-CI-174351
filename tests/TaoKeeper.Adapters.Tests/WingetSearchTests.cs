using System;
using System.Linq;
using System.Threading.Tasks;
using TaoKeeper.Adapters;
using Xunit;

namespace TaoKeeper.Adapters.Tests;

/// <summary>
/// winget search 检索通道测试（R-P2-10）：真实表头格式解析、注入词拒绝、失败如实报不假空。
/// </summary>
public class WingetSearchTests
{
    /// <summary>winget 真机口径：search 输出（中文表头，无可用列）。</summary>
    private const string SearchOutput =
        "名称                  ID                版本          源\r\n" +
        "--------------------------------------------------\r\n" +
        "网易云音乐            NetEase.Music     3.1.0         winget\r\n" +
        "网易邮箱大师          NetEase.MailMaster              winget\r\n";

    [Fact]
    public void 检索解析_真实表头输出逐条带回_版本缺省保空()
    {
        var rows = WingetOutputParser.ParseSearch(SearchOutput);

        Assert.Equal(2, rows.Count);
        Assert.Equal("网易云音乐", rows[0].Name);
        Assert.Equal("3.1.0", rows[0].AvailableVersion);
        Assert.Null(rows[1].AvailableVersion); // 未标版本如实保空
        Assert.Null(rows[0].InstalledVersion); // 检索结果无已装版本语义
    }

    [Fact]
    public async Task 检索通道_注入假Runner_参数与结果映射一致()
    {
        string? captured = null;
        var adapter = new WingetSoftwareSourceAdapter(args =>
        {
            captured = args;
            return (0, SearchOutput, string.Empty);
        });

        var rows = await adapter.SearchAsync("网易");

        Assert.Equal(2, rows.Count);
        Assert.StartsWith("search", captured);
        Assert.Contains("网易", captured);
        Assert.Contains("--accept-source-agreements", captured);
    }

    [Fact]
    public async Task 检索词白名单_注入字符拒绝_合法词放行()
    {
        Assert.False(WingetSoftwareSourceAdapter.IsSafeSearchKeyword("ab\"; rm"));
        Assert.False(WingetSoftwareSourceAdapter.IsSafeSearchKeyword("a\nb"));
        Assert.False(WingetSoftwareSourceAdapter.IsSafeSearchKeyword("   "));
        Assert.True(WingetSoftwareSourceAdapter.IsSafeSearchKeyword("7-Zip"));
        Assert.True(WingetSoftwareSourceAdapter.IsSafeSearchKeyword("微软 (Microsoft)"));

        var adapter = new WingetSoftwareSourceAdapter(_ => throw new InvalidOperationException("不该被调用"));
        Assert.Empty(await adapter.SearchAsync("ab\"; rm")); // 注入词零候选，不触进程
        Assert.Empty(await adapter.SearchAsync(new string('a', 129))); // 超长词直接拒绝，不触进程
    }

    [Fact]
    public async Task 检索失败_如实报红不假空()
    {
        var adapter = new WingetSoftwareSourceAdapter(_ => (-1, string.Empty, "winget 命令超时，已终止"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SearchAsync("7-zip"));
        Assert.Contains("超时", ex.Message);
    }
}
