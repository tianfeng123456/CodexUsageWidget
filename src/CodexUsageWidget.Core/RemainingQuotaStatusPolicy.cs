namespace CodexUsageWidget.Core;

public enum RemainingQuotaStatus
{
    WaitingForData,
    Syncing,
    Sufficient,
    Stable,
    Low,
    NearlyExhausted,
    Exhausted,
}

public static class RemainingQuotaStatusPolicy
{
    public static RemainingQuotaStatus Evaluate(
        double? remainingPercent,
        bool isRefreshing,
        bool hasLiveData)
    {
        if (!hasLiveData)
        {
            return isRefreshing
                ? RemainingQuotaStatus.Syncing
                : RemainingQuotaStatus.WaitingForData;
        }

        if (remainingPercent is not { } value ||
            !double.IsFinite(value))
        {
            return RemainingQuotaStatus.WaitingForData;
        }

        int displayedPercentage = ToDisplayPercentage(value);
        return displayedPercentage switch
        {
            >= 70 => RemainingQuotaStatus.Sufficient,
            >= 30 => RemainingQuotaStatus.Stable,
            >= 10 => RemainingQuotaStatus.Low,
            > 0 => RemainingQuotaStatus.NearlyExhausted,
            _ => RemainingQuotaStatus.Exhausted,
        };
    }

    public static int ToDisplayPercentage(double remainingPercent)
    {
        if (!double.IsFinite(remainingPercent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(remainingPercent),
                remainingPercent,
                "The remaining percentage must be finite.");
        }

        return (int)Math.Round(
            Math.Clamp(remainingPercent, 0, 100),
            MidpointRounding.AwayFromZero);
    }
}
