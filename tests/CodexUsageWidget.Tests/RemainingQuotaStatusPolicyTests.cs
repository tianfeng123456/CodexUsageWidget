using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class RemainingQuotaStatusPolicyTests
{
    [Theory]
    [InlineData(100, RemainingQuotaStatus.Sufficient)]
    [InlineData(70, RemainingQuotaStatus.Sufficient)]
    [InlineData(69.5, RemainingQuotaStatus.Sufficient)]
    [InlineData(69.49, RemainingQuotaStatus.Stable)]
    [InlineData(30, RemainingQuotaStatus.Stable)]
    [InlineData(29.5, RemainingQuotaStatus.Stable)]
    [InlineData(29.49, RemainingQuotaStatus.Low)]
    [InlineData(10, RemainingQuotaStatus.Low)]
    [InlineData(9.5, RemainingQuotaStatus.Low)]
    [InlineData(9.49, RemainingQuotaStatus.NearlyExhausted)]
    [InlineData(0.5, RemainingQuotaStatus.NearlyExhausted)]
    [InlineData(0.49, RemainingQuotaStatus.Exhausted)]
    [InlineData(0, RemainingQuotaStatus.Exhausted)]
    [InlineData(-1, RemainingQuotaStatus.Exhausted)]
    [InlineData(101, RemainingQuotaStatus.Sufficient)]
    public void Evaluate_MapsRemainingPercentageToExpectedStatus(
        double remainingPercent,
        RemainingQuotaStatus expected)
    {
        RemainingQuotaStatus actual = RemainingQuotaStatusPolicy.Evaluate(
            remainingPercent,
            isRefreshing: false,
            hasLiveData: true);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Evaluate_RefreshingLiveDataKeepsItsQuotaStatus()
    {
        RemainingQuotaStatus actual = RemainingQuotaStatusPolicy.Evaluate(
            0,
            isRefreshing: true,
            hasLiveData: true);

        Assert.Equal(RemainingQuotaStatus.Exhausted, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Evaluate_InvalidOrMissingQuotaWaitsForData(
        double? remainingPercent)
    {
        RemainingQuotaStatus actual = RemainingQuotaStatusPolicy.Evaluate(
            remainingPercent,
            isRefreshing: false,
            hasLiveData: true);

        Assert.Equal(RemainingQuotaStatus.WaitingForData, actual);
    }

    [Fact]
    public void Evaluate_NonLiveDataWaitsEvenWhenAStaleValueExists()
    {
        RemainingQuotaStatus actual = RemainingQuotaStatusPolicy.Evaluate(
            85,
            isRefreshing: false,
            hasLiveData: false);

        Assert.Equal(RemainingQuotaStatus.WaitingForData, actual);
    }

    [Fact]
    public void Evaluate_RefreshingWithoutLiveDataReportsSyncing()
    {
        RemainingQuotaStatus actual = RemainingQuotaStatusPolicy.Evaluate(
            null,
            isRefreshing: true,
            hasLiveData: false);

        Assert.Equal(RemainingQuotaStatus.Syncing, actual);
    }

    [Theory]
    [InlineData(69.49, 69)]
    [InlineData(69.5, 70)]
    [InlineData(0.49, 0)]
    [InlineData(0.5, 1)]
    [InlineData(-1, 0)]
    [InlineData(101, 100)]
    public void ToDisplayPercentage_UsesTheSameRoundedValueAsStatus(
        double remainingPercent,
        int expected)
    {
        Assert.Equal(
            expected,
            RemainingQuotaStatusPolicy.ToDisplayPercentage(
                remainingPercent));
    }
}
