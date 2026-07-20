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
        var reset = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
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
        Assert.Equal(
            10d,
            history[0].ConsumedPercentagePoints!.Value,
            6);
        Assert.Null(history[0].ChangeFromPreviousDayPercentagePoints);
        Assert.Equal(10d, history[0].LastObservedUsedPercent);
        Assert.Equal(baselineAt, history[0].LastObservedAt);
        Assert.Equal(1, history[0].ObservationCount);

        Assert.Equal(new DateOnly(2026, 7, 18), history[1].LocalDate);
        Assert.Equal(
            10d,
            history[1].ConsumedPercentagePoints!.Value,
            6);
        Assert.Equal(
            0d,
            history[1].ChangeFromPreviousDayPercentagePoints!.Value,
            6);
        Assert.Equal(20d, history[1].LastObservedUsedPercent);
        Assert.Equal(newFormatAt, history[1].LastObservedAt);
        Assert.Equal(2, history[1].ObservationCount);
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
            new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);

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
        Assert.Equal(10d, history[0].ConsumedPercentagePoints!.Value, 6);
        Assert.Equal(6d, day.ConsumedPercentagePoints!.Value, 6);
        Assert.Equal(
            -4d,
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
    public async Task QueryWeeklyRateLimitDailyUsage_ResetStartsNewEpochWithoutNegativeUsage()
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
        Assert.Equal(
            10d,
            history[0].ConsumedPercentagePoints!.Value,
            6);
        Assert.Null(history[0].ChangeFromPreviousDayPercentagePoints);
        Assert.Equal(90d, history[0].LastObservedUsedPercent);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 18, 22, 0, 0, TimeSpan.Zero),
            history[0].LastObservedAt);
        Assert.Equal(1, history[0].ObservationCount);
        Assert.True(history[0].IsPartial);

        Assert.Equal(
            12d,
            history[1].ConsumedPercentagePoints!.Value,
            6);
        Assert.Null(history[1].ChangeFromPreviousDayPercentagePoints);
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
        Assert.Equal(
            30d,
            history[0].ConsumedPercentagePoints!.Value,
            6);
        Assert.Null(history[0].ChangeFromPreviousDayPercentagePoints);
        Assert.Equal(30d, history[0].LastObservedUsedPercent);
        Assert.Equal(1, history[0].ObservationCount);
        Assert.False(history[0].IsPartial);

        Assert.Equal(new DateOnly(2026, 7, 21), history[1].LocalDate);
        Assert.Null(history[1].ConsumedPercentagePoints);
        Assert.Null(history[1].ChangeFromPreviousDayPercentagePoints);
        Assert.Null(history[1].LastObservedUsedPercent);
        Assert.Null(history[1].LastObservedAt);
        Assert.Equal(0, history[1].ObservationCount);
        Assert.True(history[1].IsPartial);

        Assert.Equal(
            5d,
            history[2].ConsumedPercentagePoints!.Value,
            6);
        Assert.Null(history[2].ChangeFromPreviousDayPercentagePoints);
        Assert.Equal(35d, history[2].LastObservedUsedPercent);
        Assert.Equal(1, history[2].ObservationCount);
        Assert.False(history[2].IsPartial);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_CrossDayFlatValueDoesNotCarryUsageForward()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = temporary.GetPath($"rollout-{RootId}.jsonl");
        var reset =
            new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 23, 0, 0, TimeSpan.Zero),
                88d,
                reset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 23, 0, 0, TimeSpan.Zero),
                88d,
                reset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero),
                89d,
                reset));
        await repository.IndexFileAsync(path, false);

        var history = await repository.QueryWeeklyRateLimitDailyUsageAsync(
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, history.Count);
        Assert.Equal(new DateOnly(2026, 7, 20), history[0].LocalDate);
        Assert.Equal(
            0d,
            history[0].ConsumedPercentagePoints!.Value,
            6);
        Assert.Equal(88d, history[0].LastObservedUsedPercent);
        Assert.False(history[0].IsPartial);

        Assert.Equal(new DateOnly(2026, 7, 21), history[1].LocalDate);
        Assert.Equal(
            1d,
            history[1].ConsumedPercentagePoints!.Value,
            6);
        Assert.Equal(
            1d,
            history[1].ChangeFromPreviousDayPercentagePoints!.Value,
            6);
        Assert.Equal(89d, history[1].LastObservedUsedPercent);
        Assert.False(history[1].IsPartial);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_StaleRollbackDoesNotChangeAcceptedObservation()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = temporary.GetPath($"rollout-{RootId}.jsonl");
        var reset =
            new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var acceptedAt =
            new DateTimeOffset(2026, 7, 21, 2, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 23, 0, 0, TimeSpan.Zero),
                40d,
                reset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero),
                41d,
                reset),
            TestLog.WeeklyRateLimit(acceptedAt, 43d, reset),
            // A concurrent session can emit an older cumulative snapshot later.
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 21, 3, 0, 0, TimeSpan.Zero),
                39d,
                reset));
        await repository.IndexFileAsync(path, false);

        var day = Assert.Single(
            await repository.QueryWeeklyRateLimitDailyUsageAsync(
                new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(3d, day.ConsumedPercentagePoints!.Value, 6);
        Assert.Equal(43d, day.LastObservedUsedPercent);
        Assert.Equal(acceptedAt, day.LastObservedAt);
        Assert.Equal(2, day.ObservationCount);
        Assert.False(day.IsPartial);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_ResetInsideDayAddsBothEpochs()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = temporary.GetPath($"rollout-{RootId}.jsonl");
        var firstReset =
            new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var secondReset = firstReset.AddDays(7);
        var lastAcceptedAt =
            new DateTimeOffset(2026, 7, 21, 13, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 23, 0, 0, TimeSpan.Zero),
                70d,
                firstReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero),
                75d,
                firstReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 21, 12, 5, 0, TimeSpan.Zero),
                4d,
                secondReset),
            // This old-window row arrived after that window had reset.
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 21, 12, 10, 0, TimeSpan.Zero),
                76d,
                firstReset),
            TestLog.WeeklyRateLimit(lastAcceptedAt, 6d, secondReset));
        await repository.IndexFileAsync(path, false);

        var day = Assert.Single(
            await repository.QueryWeeklyRateLimitDailyUsageAsync(
                new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(11d, day.ConsumedPercentagePoints!.Value, 6);
        Assert.Equal(6d, day.LastObservedUsedPercent);
        Assert.Equal(lastAcceptedAt, day.LastObservedAt);
        Assert.Equal(3, day.ObservationCount);
        Assert.False(day.IsPartial);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_MissingBaselineReportsOnlyKnownIncreaseAsPartial()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = temporary.GetPath($"rollout-{RootId}.jsonl");
        var reset =
            new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
                35d,
                reset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 22, 18, 0, 0, TimeSpan.Zero),
                40d,
                reset));
        await repository.IndexFileAsync(path, false);

        var day = Assert.Single(
            await repository.QueryWeeklyRateLimitDailyUsageAsync(
                new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(5d, day.ConsumedPercentagePoints!.Value, 6);
        Assert.Equal(40d, day.LastObservedUsedPercent);
        Assert.Equal(2, day.ObservationCount);
        Assert.True(day.IsPartial);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_ClustersResetTimestampJitterIntoOneEpoch()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var reset32 =
            new DateTimeOffset(2026, 7, 25, 3, 48, 32, TimeSpan.Zero);
        var reset33 = reset32.AddSeconds(1);
        var reset35 = reset32.AddSeconds(3);

        var mainPath = temporary.GetPath("rollout-main.jsonl");
        var variantOnePath = temporary.GetPath("rollout-variant-one.jsonl");
        var variantTwoPath = temporary.GetPath("rollout-variant-two.jsonl");
        await TestLog.WriteLinesAsync(
            mainPath,
            TestLog.SessionMeta(
                "10000000-0000-0000-0000-000000000001",
                "10000000-0000-0000-0000-000000000001"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero),
                20d,
                reset32),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 18, 20, 0, 0, TimeSpan.Zero),
                27d,
                reset32),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero),
                88d,
                reset32));
        await TestLog.WriteLinesAsync(
            variantOnePath,
            TestLog.SessionMeta(
                "20000000-0000-0000-0000-000000000002",
                "20000000-0000-0000-0000-000000000002"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 18, 20, 1, 0, TimeSpan.Zero),
                11d,
                reset33),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 10, 1, 0, TimeSpan.Zero),
                88d,
                reset33));
        await TestLog.WriteLinesAsync(
            variantTwoPath,
            TestLog.SessionMeta(
                "30000000-0000-0000-0000-000000000003",
                "30000000-0000-0000-0000-000000000003"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 18, 20, 2, 0, TimeSpan.Zero),
                5d,
                reset35),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 10, 2, 0, TimeSpan.Zero),
                88d,
                reset35));

        await repository.IndexFileAsync(mainPath, false);
        await repository.IndexFileAsync(variantOnePath, false);
        await repository.IndexFileAsync(variantTwoPath, false);

        var day = Assert.Single(
            await repository.QueryWeeklyRateLimitDailyUsageAsync(
                new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)));

        // All three reset timestamps describe the same server window. The
        // correct increase is 88 - the canonical 27 baseline, not
        // (88 - 27) + (88 - 11) + (88 - 5) = 221.
        Assert.Equal(61d, day.ConsumedPercentagePoints!.Value, 6);
        Assert.Equal(88d, day.LastObservedUsedPercent);
        Assert.False(day.IsPartial);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_DoesNotAddSparseOverlappingResetSchedule()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var canonicalReset =
            new DateTimeOffset(2026, 7, 26, 4, 0, 0, TimeSpan.Zero);
        var sparseReset = canonicalReset.AddHours(4);
        var canonicalPath = temporary.GetPath("rollout-canonical.jsonl");
        var sparsePath = temporary.GetPath("rollout-sparse.jsonl");

        await TestLog.WriteLinesAsync(
            canonicalPath,
            TestLog.SessionMeta(
                "40000000-0000-0000-0000-000000000004",
                "40000000-0000-0000-0000-000000000004"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 9, 0, 0, TimeSpan.Zero),
                30d,
                canonicalReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
                40d,
                canonicalReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
                50d,
                canonicalReset));
        await TestLog.WriteLinesAsync(
            sparsePath,
            TestLog.SessionMeta(
                "50000000-0000-0000-0000-000000000005",
                "50000000-0000-0000-0000-000000000005"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 9, 1, 0, TimeSpan.Zero),
                10d,
                sparseReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 9, 30, 0, TimeSpan.Zero),
                70d,
                sparseReset));

        await repository.IndexFileAsync(canonicalPath, false);
        await repository.IndexFileAsync(sparsePath, false);

        var day = Assert.Single(
            await repository.QueryWeeklyRateLimitDailyUsageAsync(
                new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(20d, day.ConsumedPercentagePoints!.Value, 6);
        Assert.Equal(50d, day.LastObservedUsedPercent);
        // The stronger schedule is usable, but the conflicting historical
        // schedule means this reconstructed day is not fully certain.
        Assert.True(day.IsPartial);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_MergesHigherReadingFromJitterMember()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var canonicalReset =
            new DateTimeOffset(2026, 7, 26, 4, 0, 32, TimeSpan.Zero);
        var jitteredReset = canonicalReset.AddSeconds(3);
        var canonicalPath = temporary.GetPath("rollout-canonical.jsonl");
        var jitteredPath = temporary.GetPath("rollout-jittered.jsonl");

        await TestLog.WriteLinesAsync(
            canonicalPath,
            TestLog.SessionMeta(
                "60000000-0000-0000-0000-000000000006",
                "60000000-0000-0000-0000-000000000006"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 9, 0, 0, TimeSpan.Zero),
                30d,
                canonicalReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
                40d,
                canonicalReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
                50d,
                canonicalReset));
        await TestLog.WriteLinesAsync(
            jitteredPath,
            TestLog.SessionMeta(
                "70000000-0000-0000-0000-000000000007",
                "70000000-0000-0000-0000-000000000007"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 11, 0, 0, TimeSpan.Zero),
                55d,
                jitteredReset));

        await repository.IndexFileAsync(canonicalPath, false);
        await repository.IndexFileAsync(jitteredPath, false);

        var day = Assert.Single(
            await repository.QueryWeeklyRateLimitDailyUsageAsync(
                new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(25d, day.ConsumedPercentagePoints!.Value, 6);
        Assert.Equal(55d, day.LastObservedUsedPercent);
        Assert.False(day.IsPartial);
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
