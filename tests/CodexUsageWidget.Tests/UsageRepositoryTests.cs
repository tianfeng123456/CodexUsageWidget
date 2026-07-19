using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class UsageRepositoryTests
{
    private const string RootId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task QueryPeriod_EmptyIndexReturnsZeroSummary()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);

        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero));

        Assert.Empty(snapshot.TopTasks);
        Assert.Equal(0, snapshot.Summary.TaskCount);
        Assert.Equal(TokenUsage.Zero, snapshot.Summary.Total);
        Assert.Equal(TokenUsage.Zero, snapshot.Summary.TopTasksTotal);
        Assert.Equal(TokenUsage.Zero, snapshot.Summary.OtherTasksTotal);
        Assert.Equal(0, snapshot.Summary.TopTasksPercent);
    }

    [Fact]
    public async Task IndexFile_IsIncrementalAndIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var logPath = temporary.GetPath($"rollout-{RootId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 100, 60, 8, 20, 5));

        var first = await repository.IndexFileAsync(logPath, false);
        var second = await repository.IndexFileAsync(logPath, false);
        await TestLog.AppendLinesAsync(
            logPath,
            TestLog.TokenCount(timestamp.AddMinutes(1), 150, 90, 12, 30, 8));
        var third = await repository.IndexFileAsync(logPath, false);

        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp,
            10);

        Assert.Equal(1, first.InsertedEvents);
        Assert.Equal(0, second.InsertedEvents);
        Assert.Equal(1, third.InsertedEvents);
        Assert.Equal(new TokenUsage(150, 90, 12, 30, 8), snapshot.Summary.Total);
        Assert.Equal(180, snapshot.Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task IndexFile_TruncationRemovesOldContributionBeforeRescan()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var logPath = temporary.GetPath($"rollout-{RootId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 100, 50, 0, 20, 5),
            TestLog.TokenCount(timestamp.AddMinutes(1), 200, 100, 0, 40, 10),
            TestLog.TokenCount(timestamp.AddMinutes(2), 300, 150, 0, 60, 15));
        await repository.IndexFileAsync(logPath, false);

        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 25, 10, 1, 5, 1));
        var result = await repository.IndexFileAsync(logPath, false);
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp,
            10);

        Assert.True(result.WasReset);
        Assert.Equal(new TokenUsage(25, 10, 1, 5, 1), snapshot.Summary.Total);
        Assert.Equal(30, snapshot.Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task IndexFile_RewriteWithNonShrinkingLengthUsesCheckpointHashToReset()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var logPath = temporary.GetPath($"rollout-{RootId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 100, 50, 0, 20, 5));
        await repository.IndexFileAsync(logPath, false);
        var oldLength = new FileInfo(logPath).Length;

        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 25, 10, 0, 5, 1),
            TestLog.IrrelevantHugeLine((int)Math.Max(1, oldLength)));
        var result = await repository.IndexFileAsync(logPath, false);
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp);

        Assert.True(result.WasReset);
        Assert.Equal(30, snapshot.Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task QueryPeriod_UsesLocalEventDatesForAllFourPeriods()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var logPath = temporary.GetPath($"rollout-{RootId}.jsonl");

        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(
                new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero),
                100,
                40,
                0,
                10,
                2),
            TestLog.TokenCount(
                new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero),
                200,
                80,
                3,
                20,
                4),
            TestLog.TokenCount(
                new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero),
                300,
                120,
                7,
                30,
                6));
        await repository.IndexFileAsync(logPath, false);

        var now = new DateTimeOffset(2026, 7, 18, 20, 0, 0, TimeSpan.Zero);
        var today = await repository.QueryPeriodAsync(UsagePeriod.Today, now);
        var week = await repository.QueryPeriodAsync(UsagePeriod.Last7Days, now);
        var month = await repository.QueryPeriodAsync(UsagePeriod.Month, now);
        var all = await repository.QueryPeriodAsync(UsagePeriod.All, now);

        Assert.Equal(110, today.Summary.Total.TotalTokens);
        Assert.Equal(220, week.Summary.Total.TotalTokens);
        Assert.Equal(220, month.Summary.Total.TotalTokens);
        Assert.Equal(330, all.Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task QueryPeriod_BucketsUtcTimestampUsingConfiguredLocalTimeZone()
    {
        using var temporary = new TemporaryDirectory();
        var chinaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var repository = new UsageRepository(
            temporary.GetPath("usage.db"),
            chinaTimeZone);
        await repository.InitializeAsync();
        var logPath = temporary.GetPath($"rollout-{RootId}.jsonl");

        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(
                new DateTimeOffset(2026, 7, 17, 16, 30, 0, TimeSpan.Zero),
                10,
                5,
                0,
                2,
                1));
        await repository.IndexFileAsync(logPath, false);

        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.Today,
            new DateTimeOffset(2026, 7, 18, 2, 0, 0, TimeSpan.Zero));

        Assert.Equal(12, snapshot.Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task QueryPeriod_LastSevenDays_IsExactRolling168HoursInBeijingTime()
    {
        using var temporary = new TemporaryDirectory();
        var chinaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var repository = new UsageRepository(
            temporary.GetPath("usage.db"),
            chinaTimeZone);
        await repository.InitializeAsync();
        var logPath = temporary.GetPath($"rollout-{RootId}.jsonl");
        var now = new DateTimeOffset(
            2026,
            7,
            18,
            10,
            30,
            0,
            TimeSpan.FromHours(8));
        var windowStart = now.AddDays(-7);

        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            // Outside by one second: its cumulative delta belongs to the same
            // Beijing boundary date but must not enter the rolling window.
            TestLog.TokenCount(windowStart.AddSeconds(-1), 100, 50, 1, 10, 2),
            // The lower and upper endpoints are inclusive.
            TestLog.TokenCount(windowStart, 200, 100, 2, 20, 4),
            TestLog.TokenCount(windowStart.AddSeconds(1), 300, 150, 3, 30, 6),
            // A complete interior date is served by daily_task_usage.
            TestLog.TokenCount(windowStart.AddDays(3), 400, 200, 4, 40, 8),
            TestLog.TokenCount(now, 500, 250, 5, 50, 10),
            // A future event on the current Beijing date must also be excluded.
            TestLog.TokenCount(now.AddSeconds(1), 600, 300, 6, 60, 12));
        await repository.IndexFileAsync(logPath, false);

        var week = await repository.QueryPeriodAsync(
            UsagePeriod.Last7Days,
            now);

        Assert.Equal(new DateOnly(2026, 7, 11), week.FromDate);
        Assert.Equal(new DateOnly(2026, 7, 18), week.ToDate);
        Assert.Equal(
            new TokenUsage(400, 200, 4, 40, 8),
            week.Summary.Total);
        Assert.Equal(440, week.Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task QueryPeriod_DefaultTopNinePlusOtherEqualsTotal_WithoutCacheDoubleCount()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);
        var titles = new List<SessionTitleEntry>();

        for (var index = 1; index <= 12; index++)
        {
            var id = $"00000000-0000-0000-0000-{index:000000000000}";
            var path = temporary.GetPath($"rollout-{id}.jsonl");
            await TestLog.WriteLinesAsync(
                path,
                TestLog.SessionMeta(id, id),
                TestLog.TokenCount(
                    timestamp,
                    index * 100L,
                    index * 50L,
                    index,
                    index * 10L,
                    index * 2L));
            await repository.IndexFileAsync(path, isArchived: index % 2 == 0);
            titles.Add(new SessionTitleEntry(id, $"任务 {index}", timestamp));
        }

        await repository.StoreTitlesAsync(titles);
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp);

        Assert.Equal(12, snapshot.Summary.TaskCount);
        Assert.Equal(9, snapshot.TopTasks.Count);
        Assert.Equal(
            snapshot.Summary.Total,
            snapshot.Summary.TopTasksTotal + snapshot.Summary.OtherTasksTotal);
        Assert.Equal(
            snapshot.Summary.Total.InputTokens +
            snapshot.Summary.Total.OutputTokens,
            snapshot.Summary.Total.TotalTokens);
        Assert.NotEqual(
            snapshot.Summary.Total.TotalTokens,
            snapshot.Summary.Total.TotalTokens +
            snapshot.Summary.Total.CachedInputTokens +
            snapshot.Summary.Total.ReasoningOutputTokens);
        Assert.Equal("任务 12", snapshot.TopTasks[0].Title);
        Assert.Equal(6, snapshot.Summary.OtherTasksTotal.InputTokens / 100);
    }

    [Fact]
    public async Task QueryPeriod_ReturnsLatestCodexRateLimitEvenWhenInfoIsNull()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);
        var otherPath = temporary.GetPath(
            "rollout-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.jsonl");
        var codexPath = temporary.GetPath($"rollout-{RootId}.jsonl");

        await TestLog.WriteLinesAsync(
            otherPath,
            TestLog.SessionMeta(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            TestLog.TokenCount(
                timestamp.AddMinutes(10),
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 99,
                limitId: "other"));
        await TestLog.WriteLinesAsync(
            codexPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(
                timestamp,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 20,
                limitId: "codex"));

        await repository.IndexFileAsync(otherPath, false);
        await repository.IndexFileAsync(codexPath, false);
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp);

        Assert.Equal("codex", snapshot.RateLimits?.LimitId);
        Assert.Equal(80, snapshot.RateLimits?.RemainingPercent);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_HandlesOldAndNewFormats_GlobalOrderAndDuplicates()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var reset = new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);
        var baselineAt =
            new DateTimeOffset(2026, 7, 17, 23, 0, 0, TimeSpan.Zero);
        var oldFormatAt =
            new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);
        var newFormatAt = oldFormatAt.AddHours(1);
        var highPath = temporary.GetPath(
            "rollout-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.jsonl");
        var lowPath = temporary.GetPath(
            "rollout-bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.jsonl");
        var duplicatePath = temporary.GetPath(
            "rollout-cccccccc-cccc-cccc-cccc-cccccccccccc.jsonl");

        // Index the later observation first to prove aggregation is globally
        // timestamp-ordered rather than dependent on file scan order.
        await TestLog.WriteLinesAsync(
            highPath,
            TestLog.SessionMeta(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            TestLog.WeeklyRateLimit(newFormatAt, 20d, reset),
            TestLog.WeeklyRateLimit(
                newFormatAt.AddMinutes(1),
                100d,
                reset,
                limitId: "codex_bengalfox"),
            TestLog.WeeklyRateLimit(
                newFormatAt.AddMinutes(2),
                99d,
                reset,
                windowMinutes: 300));
        await TestLog.WriteLinesAsync(
            lowPath,
            TestLog.SessionMeta(
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            TestLog.WeeklyRateLimit(baselineAt, 10d, reset),
            TestLog.WeeklyRateLimit(
                oldFormatAt,
                15d,
                reset,
                weeklyInSecondary: true),
            // A direct observation may decrease before the date's final value.
            TestLog.WeeklyRateLimit(
                oldFormatAt.AddMinutes(30),
                14d,
                reset,
                weeklyInSecondary: true));
        await TestLog.WriteLinesAsync(
            duplicatePath,
            TestLog.SessionMeta(
                "cccccccc-cccc-cccc-cccc-cccccccccccc",
                "cccccccc-cccc-cccc-cccc-cccccccccccc"),
            TestLog.WeeklyRateLimit(
                oldFormatAt,
                15d,
                reset,
                weeklyInSecondary: false));

        await repository.IndexFileAsync(highPath, false);
        await repository.IndexFileAsync(lowPath, false);
        await repository.IndexFileAsync(duplicatePath, false);

        var history = await repository.QueryWeeklyRateLimitDailyUsageAsync(
            new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, history.Count);
        Assert.Equal(new DateOnly(2026, 7, 17), history[0].LocalDate);
        Assert.Null(history[0].ChangeFromPreviousDayPercentagePoints);
        Assert.Equal(10d, history[0].LastObservedUsedPercent);
        Assert.Equal(baselineAt, history[0].LastObservedAt);
        Assert.Equal(1, history[0].ObservationCount);

        Assert.Equal(new DateOnly(2026, 7, 18), history[1].LocalDate);
        Assert.Equal(
            10d,
            history[1].ChangeFromPreviousDayPercentagePoints!.Value,
            6);
        Assert.Equal(20d, history[1].LastObservedUsedPercent);
        Assert.Equal(newFormatAt, history[1].LastObservedAt);
        Assert.Equal(3, history[1].ObservationCount);
        Assert.False(history[1].IsPartial);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_GroupsByConfiguredTimeZone()
    {
        using var temporary = new TemporaryDirectory();
        var chinaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var repository = new UsageRepository(
            temporary.GetPath("usage.db"),
            chinaTimeZone);
        await repository.InitializeAsync();
        var path = temporary.GetPath($"rollout-{RootId}.jsonl");
        var reset =
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 17, 15, 0, 0, TimeSpan.Zero),
                10d,
                reset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 17, 16, 30, 0, TimeSpan.Zero),
                16d,
                reset));
        await repository.IndexFileAsync(path, false);

        var history = await repository.QueryWeeklyRateLimitDailyUsageAsync(
            new DateTimeOffset(
                2026,
                7,
                17,
                0,
                0,
                0,
                TimeSpan.FromHours(8)),
            new DateTimeOffset(
                2026,
                7,
                19,
                0,
                0,
                0,
                TimeSpan.FromHours(8)));

        Assert.Equal(2, history.Count);
        Assert.Equal(new DateOnly(2026, 7, 17), history[0].LocalDate);
        Assert.Null(history[0].ChangeFromPreviousDayPercentagePoints);
        Assert.Equal(10d, history[0].LastObservedUsedPercent);
        Assert.Equal(
            new DateTimeOffset(
                2026,
                7,
                17,
                23,
                0,
                0,
                TimeSpan.FromHours(8)),
            history[0].LastObservedAt);

        var day = history[1];
        Assert.Equal(new DateOnly(2026, 7, 18), day.LocalDate);
        Assert.Equal(
            6d,
            day.ChangeFromPreviousDayPercentagePoints!.Value,
            6);
        Assert.Equal(16d, day.LastObservedUsedPercent);
        Assert.Equal(
            new DateTimeOffset(
                2026,
                7,
                18,
                0,
                30,
                0,
                TimeSpan.FromHours(8)),
            day.LastObservedAt);
        Assert.False(day.IsPartial);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_ResetProducesNegativeDirectChange()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = temporary.GetPath($"rollout-{RootId}.jsonl");
        var firstReset =
            new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
        var secondReset = firstReset.AddDays(7);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 18, 20, 0, 0, TimeSpan.Zero),
                80d,
                firstReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 18, 22, 0, 0, TimeSpan.Zero),
                90d,
                firstReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 0, 5, 0, TimeSpan.Zero),
                5d,
                secondReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 3, 0, 0, TimeSpan.Zero),
                12d,
                secondReset));
        await repository.IndexFileAsync(path, false);

        var history = await repository.QueryWeeklyRateLimitDailyUsageAsync(
            new DateTimeOffset(2026, 7, 18, 21, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, history.Count);
        Assert.Null(history[0].ChangeFromPreviousDayPercentagePoints);
        Assert.Equal(90d, history[0].LastObservedUsedPercent);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 18, 22, 0, 0, TimeSpan.Zero),
            history[0].LastObservedAt);
        Assert.Equal(1, history[0].ObservationCount);
        Assert.True(history[0].IsPartial);

        Assert.Equal(
            -78d,
            history[1].ChangeFromPreviousDayPercentagePoints!.Value,
            6);
        Assert.Equal(12d, history[1].LastObservedUsedPercent);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 19, 3, 0, 0, TimeSpan.Zero),
            history[1].LastObservedAt);
        Assert.Equal(2, history[1].ObservationCount);
        Assert.False(history[1].IsPartial);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_GapBreaksPreviousDayChange()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = temporary.GetPath($"rollout-{RootId}.jsonl");
        var reset = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
                30d,
                reset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
                35d,
                reset));
        await repository.IndexFileAsync(path, false);

        var history = await repository.QueryWeeklyRateLimitDailyUsageAsync(
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(3, history.Count);
        Assert.Null(history[0].ChangeFromPreviousDayPercentagePoints);
        Assert.Equal(30d, history[0].LastObservedUsedPercent);
        Assert.Equal(1, history[0].ObservationCount);
        Assert.False(history[0].IsPartial);

        Assert.Equal(new DateOnly(2026, 7, 21), history[1].LocalDate);
        Assert.Null(history[1].ChangeFromPreviousDayPercentagePoints);
        Assert.Null(history[1].LastObservedUsedPercent);
        Assert.Null(history[1].LastObservedAt);
        Assert.Equal(0, history[1].ObservationCount);
        Assert.True(history[1].IsPartial);

        Assert.Null(history[2].ChangeFromPreviousDayPercentagePoints);
        Assert.Equal(35d, history[2].LastObservedUsedPercent);
        Assert.Equal(1, history[2].ObservationCount);
        Assert.False(history[2].IsPartial);
    }

    [Fact]
    public async Task StoreTitles_DoesNotLetUndatedEntryOverwriteDatedTitle()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);
        var logPath = temporary.GetPath($"rollout-{RootId}.jsonl");
        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 10, 0, 0, 2, 0));
        await repository.IndexFileAsync(logPath, false);

        await repository.StoreTitlesAsync(
            new[] { new SessionTitleEntry(RootId, "有时间的新标题", timestamp) });
        await repository.StoreTitlesAsync(
            new[] { new SessionTitleEntry(RootId, "无时间的旧标题", null) });
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp);

        Assert.Equal("有时间的新标题", Assert.Single(snapshot.TopTasks).Title);
    }

    private static async Task<UsageRepository> CreateRepositoryAsync(
        TemporaryDirectory temporary)
    {
        var repository = new UsageRepository(
            temporary.GetPath("usage.db"),
            TimeZoneInfo.Utc);
        await repository.InitializeAsync();
        return repository;
    }
}
