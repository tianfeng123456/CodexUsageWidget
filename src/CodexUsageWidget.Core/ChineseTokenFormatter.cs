using System.Globalization;

namespace CodexUsageWidget.Core;

/// <summary>
/// Formats token counts with natural Chinese units while keeping the output
/// compact enough for the dashboard.
/// </summary>
public static class ChineseTokenFormatter
{
    private const decimal TenThousand = 10_000m;
    private const decimal OneHundredMillion = 100_000_000m;
    private const decimal OneTrillion = 1_000_000_000_000m;

    public static string Format(long tokenCount) =>
        Format((decimal)tokenCount);

    public static string Format(decimal tokenCount)
    {
        if (IsMagnitudeLessThan(tokenCount, TenThousand))
        {
            return decimal.Truncate(tokenCount).ToString(
                "#,0",
                CultureInfo.InvariantCulture);
        }

        if (IsMagnitudeLessThan(tokenCount, OneHundredMillion))
        {
            var scaled = RoundForDisplay(tokenCount / TenThousand);
            if (IsMagnitudeLessThan(scaled, TenThousand))
            {
                return FormatScaled(scaled, "万");
            }
        }

        if (IsMagnitudeLessThan(tokenCount, OneTrillion))
        {
            var scaled = RoundForDisplay(tokenCount / OneHundredMillion);
            if (IsMagnitudeLessThan(scaled, TenThousand))
            {
                return FormatScaled(scaled, "亿");
            }
        }

        return FormatScaled(
            RoundForDisplay(tokenCount / OneTrillion),
            "万亿");
    }

    private static decimal RoundForDisplay(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static bool IsMagnitudeLessThan(
        decimal value,
        decimal threshold) =>
        value > -threshold && value < threshold;

    private static string FormatScaled(decimal value, string suffix) =>
        value.ToString("0.##", CultureInfo.InvariantCulture) + suffix;
}
