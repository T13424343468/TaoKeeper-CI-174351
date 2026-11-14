using TaoKeeper.Adapters;
using Xunit;

namespace TaoKeeper.Adapters.Tests;

/// <summary>
/// defrag.exe 官方输出解析测试（R-P5-5）：中英文口径都认、取行内最后一个百分比、
/// 读不到返回 null（不估算）、异常输入安全。
/// </summary>
public class DefragOutputParserTests
{
    [Theory]
    [InlineData("Free space fragmentation: 8%", 8.0)]
    [InlineData("Total fragmentation: 12.5%", 12.5)]
    [InlineData("碎片: 12%", 12.0)]
    [InlineData("文件碎片占总数的百分比: 7 %", 7.0)]
    [InlineData("free space fragmentation: 0%", 0.0)]
    public void Parse_KnownLines_ReturnsPercent(string line, double expected)
    {
        Assert.Equal(expected, DefragOutputParser.ParseFragmentationPercent(line));
    }

    [Fact]
    public void Parse_MultiLineOutput_TakesFragmentationLine()
    {
        const string output = """
            Windows 优化驱动器
            ---------------------
            卷大小: 500 GB
            Free space: 120 GB (24%)
            Total fragmentation: 18%
            最适合优化的文件大小: 64 MB
            """;
        Assert.Equal(18.0, DefragOutputParser.ParseFragmentationPercent(output));
    }

    [Fact]
    public void Parse_ChineseFreespaceLine_FallsBackToPercentLine()
    {
        const string output = """
            卷大小: 500 GB
            可用空间: 120 GB (24%)
            空间碎片: 9%
            """;
        Assert.Equal(9.0, DefragOutputParser.ParseFragmentationPercent(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("没有可解析的输出")]
    [InlineData("Volume size: 500 GB")]
    public void Parse_UnknownOrEmpty_ReturnsNull(string output)
    {
        Assert.Null(DefragOutputParser.ParseFragmentationPercent(output));
    }

    [Fact]
    public void Parse_ValuesClampedToHundred()
    {
        // 畸形读数不外溢：>100 按上限收敛（防御性，正常官方输出不会出现）
        Assert.Equal(100.0, DefragOutputParser.ParseFragmentationPercent("Total fragmentation: 250%"));
    }
}
