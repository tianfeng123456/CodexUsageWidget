using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class ChineseTokenFormatterTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(9_999L, "9,999")]
    [InlineData(10_000L, "1万")]
    [InlineData(81_170_000L, "8117万")]
    [InlineData(99_999_999L, "1亿")]
    [InlineData(99_999_900L, "9999.99万")]
    [InlineData(100_000_000L, "1亿")]
    [InlineData(602_820_000L, "6.03亿")]
    [InlineData(10_140_000_000L, "101.4亿")]
    [InlineData(25_720_000_000L, "257.2亿")]
    [InlineData(999_999_999_999L, "1万亿")]
    [InlineData(1_000_000_000_000L, "1万亿")]
    [InlineData(long.MaxValue, "9223372.04万亿")]
    [InlineData(-10_100L, "-1.01万")]
    public void Format_UsesNaturalChineseUnits(long value, string expected)
    {
        Assert.Equal(expected, ChineseTokenFormatter.Format(value));
    }

    [Theory]
    [InlineData("10050", "1.01万")]
    [InlineData("10100", "1.01万")]
    [InlineData("12000", "1.2万")]
    [InlineData("12300", "1.23万")]
    public void Format_RoundsToTwoPlacesAndTrimsTrailingZeroes(
        string value,
        string expected)
    {
        Assert.Equal(
            expected,
            ChineseTokenFormatter.Format(decimal.Parse(value)));
    }
}
