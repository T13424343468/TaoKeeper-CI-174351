using System.Collections.Generic;
using System.Linq;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using Xunit;

namespace TaoKeeper.Core.Tests;

/// <summary>
/// MftPathBuilder 记录号→完整路径重建实测（纯托管，衔接流式扫描产出）：
/// 与 native BuildFullPath 同语义——父链拼「\」、根名不拼入、父链缺失/成环/自指拒绝；
/// 记忆化正确性（失败沿链传播、成功链逐点回填）与批量/单条入口一致性。
/// </summary>
public class MftPathBuilderTests
{
    private static MftEntryInfo E(ulong recordNo, ulong parent, string name, bool isDir = false) =>
        new(recordNo, parent, name, 0, 0, isDir, default, default, default);

    [Fact]
    public void 路径构建_树内全路径与根语义()
    {
        // 与流式扫描/聚合测试同源树：目录六(6←5)、目录七(7←6)、文件八(8←7)、文件九(9←6)、文件十(10←5)
        var entries = new List<MftEntryInfo>
        {
            E(5, 5, "卷根", isDir: true), // 根在映射内（真实 $MFT 含记录 5）
            E(6, 5, "目录六", isDir: true),
            E(7, 6, "目录七", isDir: true),
            E(8, 7, "文件八.txt"),
            E(9, 6, "文件九.txt"),
            E(10, 5, "文件十.log"),
        };
        var builder = new MftPathBuilder(entries);
        var result = builder.BuildAll();

        Assert.Equal(6, result.FullPaths.Count);
        Assert.Empty(result.FallbackRecordNos);
        Assert.Equal("", result.FullPaths[5]);          // 根自身路径空串
        Assert.Equal("目录六", result.FullPaths[6]);
        Assert.Equal("目录六\\目录七", result.FullPaths[7]);
        Assert.Equal("目录六\\目录七\\文件八.txt", result.FullPaths[8]);
        Assert.Equal("目录六\\文件九.txt", result.FullPaths[9]);
        Assert.Equal("文件十.log", result.FullPaths[10]);
    }

    [Fact]
    public void 路径构建_父链缺失_环与自指拒绝()
    {
        // 孤儿场景对齐聚合孤儿测试：父缺失(30←99)、环40↔41、环上文件(42←40)、正常(43←5)、自指(44←44)
        var entries = new List<MftEntryInfo>
        {
            E(30, 99, "父缺失.txt"),
            E(40, 41, "环甲", isDir: true),
            E(41, 40, "环乙", isDir: true),
            E(42, 40, "环上文件.txt"),
            E(43, 5, "正常.log"),
        };
        var builder = new MftPathBuilder(entries);
        var result = builder.BuildAll();

        Assert.Single(result.FullPaths);                // 只有 43 能成链
        Assert.Equal("正常.log", result.FullPaths[43]);
        Assert.Equal(new ulong[] { 30, 40, 41, 42 }, result.FallbackRecordNos.OrderBy(r => r));

        // 根 5 不在映射：查根自身判失败（同 native），不影响子链
        Assert.False(builder.TryBuildFullPath(5, out _));
        Assert.True(builder.TryBuildFullPath(43, out var path));
        Assert.Equal("正常.log", path);

        // 自指：单独补一条 44←44
        var selfEntries = new List<MftEntryInfo> { E(44, 44, "自指") };
        var selfBuilder = new MftPathBuilder(selfEntries);
        Assert.False(selfBuilder.TryBuildFullPath(44, out _));
    }

    [Fact]
    public void 路径构建_记忆化失败传播与重复查询一致()
    {
        // 链 50←49←48(缺失父)：三条全拒且失败沿记忆化传播；重复查询结果不变
        var entries = new List<MftEntryInfo>
        {
            E(48, 99, "悬空目录", isDir: true),
            E(49, 48, "子目录", isDir: true),
            E(50, 49, "孙文件.txt"),
            E(51, 5, "邻居.txt"),
        };
        var builder = new MftPathBuilder(entries);

        Assert.False(builder.TryBuildFullPath(48, out _));
        Assert.False(builder.TryBuildFullPath(49, out _)); // 首查触发链上溯命中失败记忆
        Assert.False(builder.TryBuildFullPath(50, out _)); // 深链命中失败祖先
        Assert.False(builder.TryBuildFullPath(50, out _)); // 重复查询结果不变
        Assert.True(builder.TryBuildFullPath(51, out var ok));
        Assert.Equal("邻居.txt", ok);

        var again = builder.BuildAll();
        Assert.Single(again.FullPaths);
        Assert.Equal("邻居.txt", again.FullPaths[51]);
        Assert.Equal(3, again.FallbackRecordNos.Count);
    }

    [Fact]
    public void 路径构建_同号重复首生效与乱序输入()
    {
        // 同记录号重复：首个生效（防御口径）；输入乱序（父后子前）不影响结果
        var entries = new List<MftEntryInfo>
        {
            E(9, 6, "文件九.txt"),
            E(6, 5, "目录六", isDir: true), // 父在子之后给出
            E(6, 999, "目录六假", isDir: true), // 同号重复，首个生效
        };
        var builder = new MftPathBuilder(entries);
        var result = builder.BuildAll();

        Assert.Equal("目录六\\文件九.txt", result.FullPaths[9]);
        Assert.Equal("目录六", result.FullPaths[6]);
        Assert.Empty(result.FallbackRecordNos);
    }
}
