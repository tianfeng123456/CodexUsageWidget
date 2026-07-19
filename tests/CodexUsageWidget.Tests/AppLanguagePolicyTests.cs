using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class AppLanguagePolicyTests
{
    [Theory]
    [InlineData(null, AppLanguageMode.System)]
    [InlineData("", AppLanguageMode.System)]
    [InlineData("not-a-language", AppLanguageMode.System)]
    [InlineData("System", AppLanguageMode.System)]
    [InlineData(" system ", AppLanguageMode.System)]
    [InlineData("zh-Hans", AppLanguageMode.ZhHans)]
    [InlineData("ZHHANS", AppLanguageMode.ZhHans)]
    [InlineData("en", AppLanguageMode.English)]
    [InlineData("English", AppLanguageMode.English)]
    public void ParseMode_RecognizesSupportedValues_AndDefaultsToSystem(
        string? configuredMode,
        AppLanguageMode expected)
    {
        Assert.Equal(expected, AppLanguagePolicy.ParseMode(configuredMode));
    }

    [Theory]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hant", "zh-CN")]
    [InlineData("zh-TW", "zh-CN")]
    [InlineData("ZH-SG", "zh-CN")]
    [InlineData("en-US", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("invalid", "en-US")]
    [InlineData(null, "en-US")]
    public void ResolveCultureName_SystemMode_MapsChineseAndEnglishFallback(
        string? systemCultureName,
        string expected)
    {
        Assert.Equal(
            expected,
            AppLanguagePolicy.ResolveCultureName(
                AppLanguageMode.System,
                systemCultureName));
    }

    [Fact]
    public void ResolveCultureName_ExplicitModesOverrideSystemCulture()
    {
        Assert.Equal(
            "zh-CN",
            AppLanguagePolicy.ResolveCultureName(
                AppLanguageMode.ZhHans,
                "en-US"));
        Assert.Equal(
            "en-US",
            AppLanguagePolicy.ResolveCultureName(
                AppLanguageMode.English,
                "zh-CN"));
    }

    [Fact]
    public void ResolveCultureName_InvalidConfigurationFallsBackToSystem()
    {
        Assert.Equal(
            "zh-CN",
            AppLanguagePolicy.ResolveCultureName(
                "future-language",
                "zh-Hant"));
        Assert.Equal(
            "en-US",
            AppLanguagePolicy.ResolveCultureName(
                "future-language",
                "de-DE"));
    }

    [Fact]
    public void InvalidEnumValue_IsNormalizedToSystem()
    {
        var invalid = (AppLanguageMode)999;

        Assert.Equal(
            AppLanguageMode.System,
            AppLanguagePolicy.NormalizeMode(invalid));
        Assert.Equal(
            "zh-CN",
            AppLanguagePolicy.ResolveCultureName(invalid, "zh-TW"));
        Assert.Equal("System", AppLanguagePolicy.ToSettingValue(invalid));
    }

    [Theory]
    [InlineData(AppLanguageMode.System, "System")]
    [InlineData(AppLanguageMode.ZhHans, "zh-Hans")]
    [InlineData(AppLanguageMode.English, "en")]
    public void ToSettingValue_ReturnsStablePersistedValue(
        AppLanguageMode mode,
        string expected)
    {
        Assert.Equal(expected, AppLanguagePolicy.ToSettingValue(mode));
    }
}
