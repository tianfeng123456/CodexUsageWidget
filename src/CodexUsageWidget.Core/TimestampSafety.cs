namespace CodexUsageWidget.Core;

/// <summary>
/// Keeps persisted and caller-supplied timestamps far enough from the BCL
/// endpoints for the application's seven-day arithmetic and time-zone
/// conversion to remain defined.
/// </summary>
internal static class TimestampSafety
{
    private const int ArithmeticMarginDays = 8;

    public static DateTimeOffset MinimumSupportedUtc { get; } =
        DateTimeOffset.MinValue.AddDays(ArithmeticMarginDays);

    public static DateTimeOffset MaximumSupportedUtc { get; } =
        DateTimeOffset.MaxValue.AddDays(-ArithmeticMarginDays);

    public static bool IsSupported(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return utc >= MinimumSupportedUtc && utc <= MaximumSupportedUtc;
    }

    public static void ThrowIfUnsupported(
        DateTimeOffset timestamp,
        string parameterName)
    {
        if (!IsSupported(timestamp))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "时间戳过于接近 .NET 日期边界，无法安全执行时区与周期运算。");
        }
    }
}
