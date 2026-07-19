using System.Globalization;
using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class LocalizedTokenFormatterTests
{
    private static readonly CultureInfo English =
        CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(999L, "999")]
    [InlineData(1_000L, "1K")]
    [InlineData(1_010L, "1.01K")]
    [InlineData(1_200L, "1.2K")]
    [InlineData(1_250L, "1.25K")]
    [InlineData(999_994L, "999.99K")]
    [InlineData(999_995L, "1M")]
    [InlineData(1_000_000L, "1M")]
    [InlineData(602_820_000L, "602.82M")]
    [InlineData(999_999_999L, "1B")]
    [InlineData(1_000_000_000L, "1B")]
    [InlineData(10_140_000_000L, "10.14B")]
    [InlineData(999_999_999_999L, "1T")]
    [InlineData(1_000_000_000_000L, "1T")]
    [InlineData(long.MaxValue, "9223372.04T")]
    [InlineData(-1_010L, "-1.01K")]
    public void Format_EnglishCulture_UsesCompactWesternUnits(
        long value,
        string expected)
    {
        Assert.Equal(
            expected,
            LocalizedTokenFormatter.Format(value, English));
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("zh-CN")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("zh-TW")]
    public void Format_AnyChineseCulture_PreservesNaturalChineseRules(
        string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);

        Assert.Equal(
            "8117万",
            LocalizedTokenFormatter.Format(81_170_000L, culture));
        Assert.Equal(
            "6.03亿",
            LocalizedTokenFormatter.Format(602_820_000L, culture));
    }

    [Fact]
    public void Format_ExplicitModeOverridesSystemCulture()
    {
        Assert.Equal(
            "1万",
            LocalizedTokenFormatter.Format(
                10_000L,
                AppLanguageMode.ZhHans,
                "en-US"));
        Assert.Equal(
            "10K",
            LocalizedTokenFormatter.Format(
                10_000L,
                AppLanguageMode.English,
                "zh-CN"));
    }

    [Fact]
    public void Format_SystemModeUsesLanguagePolicy()
    {
        Assert.Equal(
            "1万",
            LocalizedTokenFormatter.Format(
                10_000L,
                AppLanguageMode.System,
                "zh-Hant"));
        Assert.Equal(
            "10K",
            LocalizedTokenFormatter.Format(
                10_000L,
                AppLanguageMode.System,
                "fr-FR"));
    }

    [Fact]
    public void Format_DecimalRoundsToTwoPlacesAndTrimsTrailingZeroes()
    {
        Assert.Equal(
            "1.01K",
            LocalizedTokenFormatter.Format(1_005m, English));
        Assert.Equal(
            "1.2K",
            LocalizedTokenFormatter.Format(1_200m, English));
    }

    [Fact]
    public void Format_DecimalMinValue_DoesNotOverflow()
    {
        Assert.Equal(
            "-79228162514264337.59T",
            LocalizedTokenFormatter.Format(decimal.MinValue, English));
        Assert.Equal(
            "-79228162514264337.59万亿",
            LocalizedTokenFormatter.Format(
                decimal.MinValue,
                CultureInfo.GetCultureInfo("zh-CN")));
    }
}
