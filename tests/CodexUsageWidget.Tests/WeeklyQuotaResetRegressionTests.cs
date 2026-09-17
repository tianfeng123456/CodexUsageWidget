using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class WeeklyQuotaResetRegressionTests
{
    [Theory]
    [InlineData(0d)]
    [InlineData(6d)]
    public async Task ManualReset_PreservesConsumptionBeforeReset(double afterReset)
    {
        using var temporary = new TemporaryDirectory();
        var repository = new UsageRepository(temporary.GetPath("usage.db"), TimeZoneInfo.Utc);
        await repository.InitializeAsync();
        var day = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        var oldReset = day.AddDays(2);
        var manualResetAt = day.AddHours(20);
        var newReset = manualResetAt.AddDays(7);
        var path = temporary.GetPath("rollout-reset.jsonl");
        await TestLog.WriteLinesAsync(path,
            TestLog.SessionMeta("11111111-1111-1111-1111-111111111111"),
            TestLog.WeeklyRateLimit(day.AddHours(-1), 50, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(18), 95, oldReset),
            TestLog.WeeklyRateLimit(manualResetAt.AddSeconds(15), afterReset, newReset));
        await repository.IndexFileAsync(path, false);

        var result = Assert.Single(await repository.QueryWeeklyRateLimitDailyUsageAsync(day, day.AddDays(1)));

        Assert.Equal(45d + afterReset, result.ConsumedPercentagePoints);
        Assert.Equal(afterReset, result.LastObservedUsedPercent);
        Assert.False(result.IsPartial);
    }

    [Fact]
    public async Task MultipleManualResets_AddEveryObservedSegmentBeyondOneHundredPercent()
    {
        var day = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        var oldReset = day.AddDays(2);
        var firstReset = day.AddHours(10).AddDays(7);
        var secondReset = day.AddHours(14).AddDays(7);
        var result = Assert.Single(await QueryAsync(day, day.AddDays(1),
            TestLog.WeeklyRateLimit(day.AddHours(-1), 10, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(8), 90, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(10), 0, firstReset),
            TestLog.WeeklyRateLimit(day.AddHours(12), 90, firstReset),
            TestLog.WeeklyRateLimit(day.AddHours(14), 0, secondReset),
            TestLog.WeeklyRateLimit(day.AddHours(20), 40, secondReset)));

        Assert.Equal(210d, result.ConsumedPercentagePoints);
        Assert.Equal(40d, result.LastObservedUsedPercent);
        Assert.Equal(5, result.ObservationCount);
        Assert.False(result.IsPartial);
    }

    [Fact]
    public async Task RepeatedResetWithoutInterveningUsage_DoesNotBreakTheEarlierChain()
    {
        var day = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        var oldReset = day.AddDays(2);
        var firstReset = day.AddHours(10).AddDays(7);
        var secondReset = day.AddHours(14).AddDays(7);
        var result = Assert.Single(await QueryAsync(day, day.AddDays(1),
            TestLog.WeeklyRateLimit(day.AddHours(-1), 10, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(8), 90, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(10), 0, firstReset),
            TestLog.WeeklyRateLimit(day.AddHours(14), 0, secondReset),
            TestLog.WeeklyRateLimit(day.AddHours(20), 40, secondReset)));

        Assert.Equal(120d, result.ConsumedPercentagePoints);
        Assert.Equal(40d, result.LastObservedUsedPercent);
    }

    [Fact]
    public async Task LateOldScheduleAfterManualReset_DoesNotOverwriteOrInflateTheDay()
    {
        var day = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        var oldReset = day.AddDays(2);
        var newReset = day.AddHours(20).AddDays(7);
        var result = Assert.Single(await QueryAsync(day, day.AddDays(1),
            TestLog.WeeklyRateLimit(day.AddHours(-1), 50, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(18), 95, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(20), 0, newReset),
            TestLog.WeeklyRateLimit(day.AddHours(21), 6, newReset),
            TestLog.WeeklyRateLimit(day.AddHours(22), 99, oldReset)));

        Assert.Equal(51d, result.ConsumedPercentagePoints);
        Assert.Equal(6d, result.LastObservedUsedPercent);
        Assert.Equal(day.AddHours(21), result.LastObservedAt);
        Assert.Equal(3, result.ObservationCount);
    }

    [Fact]
    public async Task ManualResetNearMidnight_AttributesEachDaysOwnConsumption()
    {
        var day = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        var oldReset = day.AddDays(2);
        var resetAt = day.AddHours(23).AddMinutes(55);
        var newReset = resetAt.AddDays(7);
        var result = await QueryAsync(day, day.AddDays(2),
            TestLog.WeeklyRateLimit(day.AddHours(-1), 51, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(23), 98, oldReset),
            TestLog.WeeklyRateLimit(resetAt, 0, newReset),
            TestLog.WeeklyRateLimit(resetAt.AddMinutes(1), 2, newReset),
            TestLog.WeeklyRateLimit(day.AddDays(1).AddHours(1), 6, newReset));

        Assert.Equal(49d, result[0].ConsumedPercentagePoints);
        Assert.Equal(4d, result[1].ConsumedPercentagePoints);
        Assert.Equal(-45d, result[1].ChangeFromPreviousDayPercentagePoints);
        Assert.All(result, value => Assert.False(value.IsPartial));
    }

    [Fact]
    public async Task ManualResetWithoutEarlierBaseline_KeepsPartialFlag()
    {
        var day = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        var oldReset = day.AddDays(2);
        var newReset = day.AddHours(20).AddDays(7);
        var result = Assert.Single(await QueryAsync(day, day.AddDays(1),
            TestLog.WeeklyRateLimit(day.AddHours(8), 50, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(18), 95, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(20), 0, newReset)));

        Assert.Equal(45d, result.ConsumedPercentagePoints);
        Assert.True(result.IsPartial);
        Assert.Null(result.ChangeFromPreviousDayPercentagePoints);
    }

    [Fact]
    public async Task UnrelatedOverlappingSchedule_IsNotAddedAsAManualReset()
    {
        var day = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        var oldReset = day.AddDays(2);
        // Both schedules started days ago: a lower reading alone is no reset.
        var unrelatedReset = day.AddDays(3);
        var result = Assert.Single(await QueryAsync(day, day.AddDays(1),
            TestLog.WeeklyRateLimit(day.AddHours(-1), 50, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(-1).AddMinutes(1), 10, unrelatedReset),
            TestLog.WeeklyRateLimit(day.AddHours(18), 95, oldReset),
            TestLog.WeeklyRateLimit(day.AddHours(20), 16, unrelatedReset)));

        Assert.Equal(6d, result.ConsumedPercentagePoints);
        Assert.Equal(16d, result.LastObservedUsedPercent);
    }

    private static async Task<IReadOnlyList<DailyWeeklyRateLimitUsage>> QueryAsync(
        DateTimeOffset from, DateTimeOffset to, params string[] observations)
    {
        using var temporary = new TemporaryDirectory();
        var repository = new UsageRepository(temporary.GetPath("usage.db"), TimeZoneInfo.Utc);
        await repository.InitializeAsync();
        var path = temporary.GetPath("rollout-reset.jsonl");
        await TestLog.WriteLinesAsync(path,
            [TestLog.SessionMeta("11111111-1111-1111-1111-111111111111"), .. observations]);
        await repository.IndexFileAsync(path, false);
        return await repository.QueryWeeklyRateLimitDailyUsageAsync(from, to);
    }
}
