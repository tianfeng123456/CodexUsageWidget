using System.Globalization;

namespace CodexUsageWidget.Core;

/// <summary>
/// Formats token counts using natural Chinese units for Chinese cultures and
/// compact K/M/B/T units for every other supported culture.
/// </summary>
public static class LocalizedTokenFormatter
{
    private const decimal OneThousand = 1_000m;
    private const decimal OneMillion = 1_000_000m;
    private const decimal OneBillion = 1_000_000_000m;
    private const decimal OneTrillion = 1_000_000_000_000m;

    private static readonly CultureInfo EnglishCulture =
        CultureInfo.GetCultureInfo(AppLanguagePolicy.EnglishCultureName);

    public static string Format(long tokenCount, CultureInfo culture) =>
        Format((decimal)tokenCount, culture);

    public static string Format(decimal tokenCount, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        return AppLanguagePolicy.IsChineseCultureName(culture.Name)
            ? ChineseTokenFormatter.Format(tokenCount)
            : FormatEnglish(tokenCount);
    }

    public static string Format(
        long tokenCount,
        AppLanguageMode mode,
        string? systemCultureName) =>
        Format((decimal)tokenCount, mode, systemCultureName);

    public static string Format(
        decimal tokenCount,
        AppLanguageMode mode,
        string? systemCultureName)
    {
        return Format(
            tokenCount,
            AppLanguagePolicy.ResolveCulture(mode, systemCultureName));
    }

    private static string FormatEnglish(decimal tokenCount)
    {
        if (IsMagnitudeLessThan(tokenCount, OneThousand))
        {
            return decimal.Truncate(tokenCount).ToString(
                "#,0",
                EnglishCulture);
        }

        if (IsMagnitudeLessThan(tokenCount, OneMillion))
        {
            var scaled = RoundForDisplay(tokenCount / OneThousand);
            if (IsMagnitudeLessThan(scaled, OneThousand))
            {
                return FormatScaled(scaled, "K");
            }
        }

        if (IsMagnitudeLessThan(tokenCount, OneBillion))
        {
            var scaled = RoundForDisplay(tokenCount / OneMillion);
            if (IsMagnitudeLessThan(scaled, OneThousand))
            {
                return FormatScaled(scaled, "M");
            }
        }

        if (IsMagnitudeLessThan(tokenCount, OneTrillion))
        {
            var scaled = RoundForDisplay(tokenCount / OneBillion);
            if (IsMagnitudeLessThan(scaled, OneThousand))
            {
                return FormatScaled(scaled, "B");
            }
        }

        return FormatScaled(
            RoundForDisplay(tokenCount / OneTrillion),
            "T");
    }

    private static decimal RoundForDisplay(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static bool IsMagnitudeLessThan(
        decimal value,
        decimal threshold) =>
        value > -threshold && value < threshold;

    private static string FormatScaled(decimal value, string suffix) =>
        value.ToString("0.##", EnglishCulture) + suffix;
}
