using CodexUsageWidget.Core;
using Microsoft.Data.Sqlite;

namespace CodexUsageWidget.Tests;

public sealed class UsageIndexServiceTests
{
    [Fact]
    public async Task Facade_InitializesRefreshesQueriesAndReportsProgress()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions", "2026", "07", "18");
        Directory.CreateDirectory(sessions);
        const string id = "11111111-1111-1111-1111-111111111111";
        var logPath = System.IO.Path.Combine(sessions, $"rollout-{id}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(id, id),
            TestLog.TokenCount(timestamp, 100, 50, 3, 20, 5, usedPercent: 20));
        var sessionIndex = System.IO.Path.Combine(home, "session_index.jsonl");
        await TestLog.WriteLinesAsync(
            sessionIndex,
            """{"id":"11111111-1111-1111-1111-111111111111","thread_name":"门面测试任务","updated_at":"2026-07-18T00:00:00Z"}""");

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        var progressEvents = new List<IndexProgressChangedEventArgs>();
        service.ProgressChanged += (_, args) => progressEvents.Add(args);

        await service.InitializeAsync();
        var snapshot = await service.QueryPeriodAsync(
            UsagePeriod.Today,
            timestamp);
        var dashboard = await service.GetDashboardAsync(UsagePeriod.Today);

        Assert.Equal(120, snapshot.Summary.Total.TotalTokens);
        Assert.Equal("门面测试任务", Assert.Single(snapshot.TopTasks).Title);
        Assert.Equal(80, dashboard.RateLimits?.RemainingPercent);
        Assert.Contains(progressEvents, static args => args.IsComplete);
        Assert.Equal(IndexProgressStage.Preparing, progressEvents[0].Stage);
        Assert.Contains(
            progressEvents,
            static args => args.Stage == IndexProgressStage.Reading);
        Assert.Contains(
            progressEvents,
            static args => args.Stage == IndexProgressStage.Finalizing);
        Assert.Equal(IndexProgressStage.Completed, progressEvents[^1].Stage);
        Assert.Equal(0d, progressEvents[0].Progress);
        Assert.Equal(1d, progressEvents[^1].Progress);
        Assert.Equal(1d, service.IndexProgress);
        var expectedBytes = new FileInfo(logPath).Length +
                            new FileInfo(sessionIndex).Length;
        var determinate = progressEvents
            .Where(static progress => progress.TotalBytes > 0)
            .ToArray();
        Assert.NotEmpty(determinate);
        Assert.All(
            determinate,
            progress => Assert.Equal(expectedBytes, progress.TotalBytes));
        Assert.True(
            determinate.Zip(
                    determinate.Skip(1),
                    static (left, right) =>
                        right.ProcessedBytes >= left.ProcessedBytes &&
                        right.Progress >= left.Progress)
                .All(static monotonic => monotonic));
        Assert.Contains(
            determinate,
            progress => string.Equals(
                progress.CurrentFile,
                sessionIndex,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Facade_QueriesIndexedWeeklyRateLimitDailyUsageWithoutRefresh()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        const string id = "44444444-4444-4444-4444-444444444444";
        var path = System.IO.Path.Combine(sessions, $"rollout-{id}.jsonl");
        var reset = new DateTimeOffset(2026, 7, 23, 23, 0, 0, TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(id, id),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 17, 23, 0, 0, TimeSpan.Zero),
                40d,
                reset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero),
                46d,
                reset));

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        await service.InitializeAsync();

        var history = await service.QueryWeeklyRateLimitDailyUsageAsync(
            new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, history.Count);
        Assert.Equal(0d, history[0].ConsumedPercentagePoints);
        Assert.Null(history[0].ChangeFromPreviousDayPercentagePoints);
        var day = history[1];
        Assert.Equal(6d, day.ConsumedPercentagePoints);
        Assert.Null(day.ChangeFromPreviousDayPercentagePoints);
        Assert.Equal(46d, day.LastObservedUsedPercent);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero),
            day.LastObservedAt);
        Assert.False(day.IsPartial);
    }

    [Fact]
    public async Task RecentRateLimitRead_PrefersCodexThenNewestSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var start = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);
        var oldCodex = System.IO.Path.Combine(sessions, "rollout-old-codex.jsonl");
        var newCodex = System.IO.Path.Combine(sessions, "rollout-new-codex.jsonl");
        var newestOther = System.IO.Path.Combine(sessions, "rollout-other.jsonl");

        await TestLog.WriteLinesAsync(
            oldCodex,
            TestLog.TokenCount(start, 0, 0, 0, 0, 0, usedPercent: 20));
        await TestLog.WriteLinesAsync(
            newCodex,
            TestLog.TokenCount(
                start.AddMinutes(1),
                0,
                0,
                0,
                0,
                0,
                usedPercent: 30));
        await TestLog.WriteLinesAsync(
            newestOther,
            TestLog.TokenCount(
                start.AddMinutes(2),
                0,
                0,
                0,
                0,
                0,
                usedPercent: 99,
                limitId: "other"));

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZoneInfo.Utc));
        await service.InitializeAsync();

        var snapshot = await service.ReadLatestRateLimitsFromRecentLogsAsync();

        Assert.NotNull(snapshot);
        Assert.Equal("codex", snapshot.LimitId);
        Assert.Equal(start.AddMinutes(1), snapshot.Timestamp);
        Assert.Equal(70d, snapshot.RemainingPercent);
    }

    [Fact]
    public async Task OpenAsync_LoadsExistingIndexWithoutScanning_AndReadsQuotaFromTail()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions", "2026", "07", "18");
        Directory.CreateDirectory(sessions);
        const string id = "11111111-1111-1111-1111-111111111111";
        var logPath = System.IO.Path.Combine(sessions, $"rollout-{id}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(id, id),
            TestLog.TokenCount(
                timestamp,
                100,
                50,
                3,
                20,
                5,
                usedPercent: 25));

        var databasePath = temporary.GetPath("usage.db");
        await using (var firstRun = new UsageIndexService(
                         new UsageIndexOptions(
                             CodexHome: home,
                             DatabasePath: databasePath,
                             TimeZone: TimeZoneInfo.Utc)))
        {
            await firstRun.InitializeAsync();
        }

        await TestLog.AppendLinesAsync(
            logPath,
            TestLog.TokenCount(
                timestamp.AddMinutes(1),
                180,
                90,
                4,
                30,
                8,
                usedPercent: 30));

        await using var reopened = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: databasePath,
                TimeZone: TimeZoneInfo.Utc));
        var progressEvents = new List<IndexProgressChangedEventArgs>();
        reopened.ProgressChanged += (_, args) => progressEvents.Add(args);

        await reopened.OpenAsync();
        var cached = await reopened.QueryPeriodAsync(
            UsagePeriod.Today,
            timestamp);
        var fastRate = await reopened.ReadLatestRateLimitsFromRecentLogsAsync();

        Assert.Equal(120, cached.Summary.Total.TotalTokens);
        Assert.Empty(progressEvents);
        Assert.Equal(timestamp.AddMinutes(1), fastRate?.Timestamp);
        Assert.Equal(70, fastRate?.RemainingPercent);

        await reopened.RefreshAsync();
        var refreshed = await reopened.QueryPeriodAsync(
            UsagePeriod.Today,
            timestamp);

        Assert.Equal(210, refreshed.Summary.Total.TotalTokens);
        Assert.Contains(progressEvents, static args => args.IsComplete);
    }

    [Fact]
    public async Task RefreshAsync_CoalescesProgressForManyUnchangedFiles()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var timestamp = new DateTimeOffset(
            2026,
            7,
            18,
            1,
            0,
            0,
            TimeSpan.Zero);

        for (var index = 0; index < 64; index++)
        {
            var id = Guid.NewGuid().ToString();
            await TestLog.WriteLinesAsync(
                System.IO.Path.Combine(sessions, $"rollout-{id}.jsonl"),
                TestLog.SessionMeta(id, id),
                TestLog.TokenCount(
                    timestamp.AddSeconds(index),
                    100,
                    50,
                    0,
                    20,
                    5));
        }

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        await service.InitializeAsync();

        var progressEvents = new List<IndexProgressChangedEventArgs>();
        service.ProgressChanged += (_, args) => progressEvents.Add(args);
        var refresh = await service.RefreshAsync();

        Assert.Equal(0, refresh.FilesChanged);
        Assert.Equal(0, refresh.BytesProcessed);
        Assert.InRange(
            progressEvents.Count(
                static args => args.Stage == IndexProgressStage.Reading),
            2,
            4);
        Assert.Equal(IndexProgressStage.Preparing, progressEvents[0].Stage);
        Assert.Equal(IndexProgressStage.Finalizing, progressEvents[^2].Stage);
        Assert.True(progressEvents[^1].IsComplete);
    }

    [Fact]
    public async Task RefreshAsync_ReportsIncompleteForLockedLog_AndRetryCompletes()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        const string id = "77777777-7777-7777-7777-777777777777";
        var path = System.IO.Path.Combine(sessions, $"rollout-{id}.jsonl");
        var timestamp = new DateTimeOffset(
            2026,
            7,
            18,
            1,
            0,
            0,
            TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(id, id),
            TestLog.TokenCount(timestamp, 100, 50, 0, 20, 5));

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        await service.OpenAsync();
        var progressEvents = new List<IndexProgressChangedEventArgs>();
        service.ProgressChanged += (_, args) => progressEvents.Add(args);

        RefreshResult incomplete;
        await using (var locked = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            incomplete = await service.RefreshAsync();
        }

        Assert.False(incomplete.CompletedSuccessfully);
        Assert.False(service.HasCompletedInitialIndex);
        Assert.True(service.RequiresHistoryBuild);
        Assert.Equal(IndexProgressStage.Incomplete, progressEvents[^1].Stage);
        Assert.True(progressEvents[^1].IsTerminal);
        Assert.False(progressEvents[^1].IsComplete);

        progressEvents.Clear();
        var retry = await service.RefreshAsync();

        Assert.True(retry.CompletedSuccessfully);
        Assert.True(service.HasCompletedInitialIndex);
        Assert.False(service.RequiresHistoryBuild);
        Assert.Equal(IndexProgressStage.Preparing, progressEvents[0].Stage);
        Assert.Equal(IndexProgressStage.Completed, progressEvents[^1].Stage);
        Assert.True(progressEvents[^1].IsComplete);
    }

    [Fact]
    public async Task RefreshAsync_ResolvesForkThatSortsBeforeParentInSamePass()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        const string parentId = "99999999-9999-9999-9999-999999999999";
        const string forkId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var timestamp = new DateTimeOffset(
            2026,
            7,
            28,
            21,
            0,
            0,
            TimeSpan.Zero);
        var forkPath = System.IO.Path.Combine(sessions, "a-fork.jsonl");
        var parentPath = System.IO.Path.Combine(sessions, "z-parent.jsonl");
        await TestLog.WriteLinesAsync(
            forkPath,
            TestLog.SessionMeta(
                forkId,
                forkId,
                timestamp: timestamp,
                forkedFromId: parentId,
                threadSource: "subagent"),
            TestLog.TokenCount(
                timestamp.AddSeconds(1),
                200,
                160,
                0,
                20,
                10),
            TestLog.TokenCount(
                timestamp.AddSeconds(2),
                250,
                200,
                0,
                25,
                12));
        await TestLog.WriteLinesAsync(
            parentPath,
            TestLog.SessionMeta(parentId, parentId, timestamp: timestamp),
            TestLog.TokenCount(
                timestamp.AddSeconds(1),
                200,
                160,
                0,
                20,
                10));

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        await service.OpenAsync();
        var progressEvents = new List<IndexProgressChangedEventArgs>();
        service.ProgressChanged += (_, args) => progressEvents.Add(args);

        var result = await service.RefreshAsync();

        Assert.True(result.CompletedSuccessfully);
        Assert.True(service.HasCompletedInitialIndex);
        Assert.False(service.RequiresHistoryBuild);
        Assert.Equal(IndexProgressStage.Completed, progressEvents[^1].Stage);
        Assert.Equal(
            new TokenUsage(250, 200, 0, 25, 12),
            (await service.QueryPeriodAsync(
                UsagePeriod.All,
                timestamp.AddHours(1))).Summary.Total);
    }

    [Fact]
    public async Task RefreshAsync_MissingForkParentRemainsExplicitlyIncomplete()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        const string forkId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string missingParentId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        var timestamp = new DateTimeOffset(
            2026,
            7,
            28,
            21,
            0,
            0,
            TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            System.IO.Path.Combine(sessions, "orphan-fork.jsonl"),
            TestLog.SessionMeta(
                forkId,
                forkId,
                timestamp: timestamp,
                forkedFromId: missingParentId,
                threadSource: "subagent"),
            TestLog.TokenCount(
                timestamp.AddSeconds(1),
                200,
                160,
                0,
                20,
                10));

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        await service.OpenAsync();
        var progressEvents = new List<IndexProgressChangedEventArgs>();
        service.ProgressChanged += (_, args) => progressEvents.Add(args);

        var result = await service.RefreshAsync();

        Assert.False(result.CompletedSuccessfully);
        Assert.False(service.HasCompletedInitialIndex);
        Assert.True(service.RequiresHistoryBuild);
        Assert.Equal(IndexProgressStage.Incomplete, progressEvents[^1].Stage);
        Assert.Equal(
            0,
            (await service.QueryPeriodAsync(
                UsagePeriod.All,
                timestamp.AddHours(1))).Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task RebuildIndexAsync_ReportsPreparationBeforeResetAndReturnsSuccess()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        Directory.CreateDirectory(System.IO.Path.Combine(home, "sessions"));

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        await service.InitializeAsync();
        var progressEvents = new List<IndexProgressChangedEventArgs>();
        service.ProgressChanged += (_, args) => progressEvents.Add(args);

        var result = await service.RebuildIndexAsync();

        Assert.True(result.CompletedSuccessfully);
        Assert.Equal(IndexProgressStage.Preparing, progressEvents[0].Stage);
        Assert.Equal(0d, progressEvents[0].Progress);
        Assert.Equal(IndexProgressStage.Completed, progressEvents[^1].Stage);
    }

    [Fact]
    public async Task RefreshAsync_CancellationDoesNotReportAFalseFailure()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        const string id = "88888888-8888-8888-8888-888888888888";
        var path = System.IO.Path.Combine(sessions, $"rollout-{id}.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(id, id),
            TestLog.TokenCount(
                new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero),
                100,
                50,
                0,
                20,
                5));

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        await service.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var progressEvents = new List<IndexProgressChangedEventArgs>();
        service.ProgressChanged += (_, args) =>
        {
            progressEvents.Add(args);
            if (args.Stage == IndexProgressStage.Reading)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RefreshAsync(cancellation.Token));

        Assert.Contains(
            progressEvents,
            static args => args.Stage == IndexProgressStage.Reading);
        Assert.DoesNotContain(
            progressEvents,
            static args => args.Stage == IndexProgressStage.Incomplete);
        Assert.False(service.HasCompletedInitialIndex);
    }

    [Fact]
    public async Task InitialIndexMarker_IsFalseBeforeFirstRefresh_AndPersists()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        Directory.CreateDirectory(System.IO.Path.Combine(home, "sessions"));
        var databasePath = temporary.GetPath("usage.db");

        await using (var firstRun = new UsageIndexService(
                         new UsageIndexOptions(
                             CodexHome: home,
                             DatabasePath: databasePath,
                             TimeZone: TimeZoneInfo.Utc)))
        {
            await firstRun.OpenAsync();
            Assert.False(firstRun.HasCompletedInitialIndex);

            await firstRun.RefreshAsync();
            Assert.True(firstRun.HasCompletedInitialIndex);
        }

        await using var reopened = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: databasePath,
                TimeZone: TimeZoneInfo.Utc));
        await reopened.OpenAsync();

        Assert.True(reopened.HasCompletedInitialIndex);
    }

    [Fact]
    public async Task OpenAsync_AcceptsLegacyLastRefreshMarker()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        Directory.CreateDirectory(System.IO.Path.Combine(home, "sessions"));
        var databasePath = temporary.GetPath("usage.db");

        await using (var firstRun = new UsageIndexService(
                         new UsageIndexOptions(
                             CodexHome: home,
                             DatabasePath: databasePath,
                             TimeZone: TimeZoneInfo.Utc)))
        {
            await firstRun.OpenAsync();
            await firstRun.RefreshAsync();
        }

        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = databasePath
                         }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM metadata WHERE key = 'initial_index_complete';";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await using var reopened = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: databasePath,
                TimeZone: TimeZoneInfo.Utc));
        await reopened.OpenAsync();

        Assert.True(reopened.HasCompletedInitialIndex);
    }

    [Fact]
    public async Task RebuildIndexAsync_RestoresCompletedMarker()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        Directory.CreateDirectory(System.IO.Path.Combine(home, "sessions"));

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        await service.OpenAsync();

        await service.RebuildIndexAsync();

        Assert.True(service.HasCompletedInitialIndex);
    }

    [Fact]
    public async Task FirstRefresh_DoesNotCompleteMarkerUntilLockedFileCanBeRead()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        const string id = "22222222-2222-2222-2222-222222222222";
        var logPath = System.IO.Path.Combine(sessions, $"rollout-{id}.jsonl");
        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(id, id),
            TestLog.TokenCount(
                new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero),
                100,
                50,
                0,
                20,
                5));

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        await service.OpenAsync();

        await using (var locked = new FileStream(
                         logPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.None))
        {
            await service.RefreshAsync();
            Assert.False(service.HasCompletedInitialIndex);
        }

        await service.RefreshAsync();

        Assert.True(service.HasCompletedInitialIndex);
        Assert.Equal(
            120,
            (await service.QueryPeriodAsync(
                UsagePeriod.All,
                new DateTimeOffset(2026, 7, 18, 2, 0, 0, TimeSpan.Zero)))
            .Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task RefreshAsync_MigratesVersionOneAccountingIndexOnce()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        const string id = "33333333-3333-3333-3333-333333333333";
        var logPath = System.IO.Path.Combine(sessions, $"rollout-{id}.jsonl");
        var timestamp = new DateTimeOffset(
            2026,
            7,
            28,
            4,
            15,
            0,
            TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(id, id),
            TestLog.TokenCount(
                timestamp,
                603_764_545,
                500_000_000,
                0,
                1_000,
                100),
            TestLog.TokenCount(
                timestamp.AddSeconds(1),
                603_754_655,
                499_999_000,
                0,
                999,
                99),
            TestLog.TokenCount(
                timestamp.AddSeconds(2),
                603_800_000,
                500_020_000,
                0,
                1_100,
                110));

        var databasePath = temporary.GetPath("usage.db");
        await using (var firstRun = new UsageIndexService(
                         new UsageIndexOptions(
                             CodexHome: home,
                             DatabasePath: databasePath,
                             TimeZone: TimeZoneInfo.Utc)))
        {
            await firstRun.InitializeAsync();
        }

        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = databasePath
                         }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE file_state
                SET token_accounting_version = 1;

                UPDATE daily_task_usage
                SET input_tokens = 12341267040,
                    output_tokens = 1100;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var migrated = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: databasePath,
                TimeZone: TimeZoneInfo.Utc));
        await migrated.OpenAsync();
        var cached = await migrated.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));
        Assert.Equal(12_341_268_140, cached.Summary.Total.TotalTokens);

        var migration = await migrated.RefreshAsync();
        var corrected = await migrated.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));
        var repeated = await migrated.RefreshAsync();

        Assert.True(migration.FilesChanged > 0);
        Assert.True(migration.BytesProcessed > 0);
        Assert.Equal(
            new TokenUsage(603_800_000, 500_020_000, 0, 1_100, 110),
            corrected.Summary.Total);
        Assert.Equal(603_801_100, corrected.Summary.Total.TotalTokens);
        Assert.True(migrated.HasCompletedInitialIndex);
        Assert.Equal(0, repeated.FilesChanged);
        Assert.Equal(0, repeated.BytesProcessed);

        await using var verify = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString());
        await verify.OpenAsync();
        await using var version = verify.CreateCommand();
        version.CommandText =
            "SELECT token_accounting_version FROM file_state LIMIT 1;";
        Assert.Equal(
            4L,
            Convert.ToInt64(
                await version.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task OpenAsync_QuarantinesCorruptDerivedIndexAndStartsFresh()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        Directory.CreateDirectory(System.IO.Path.Combine(home, "sessions"));
        var databasePath = temporary.GetPath("usage.db");
        await File.WriteAllTextAsync(databasePath, "this is not a sqlite database");

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: databasePath,
                TimeZone: TimeZoneInfo.Utc));

        await service.OpenAsync();

        Assert.False(service.HasCompletedInitialIndex);
        Assert.True(service.RequiresHistoryBuild);
        Assert.True(File.Exists(databasePath));
        Assert.Single(Directory.GetFiles(
            temporary.Path,
            "usage.db.corrupt-*",
            SearchOption.TopDirectoryOnly));
        Assert.True(File.Exists(temporary.GetPath("diagnostics.log")));
    }

    [Fact]
    public async Task RebuildIndexAsync_RecoversCorruptionDetectedAfterOpen()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        Directory.CreateDirectory(System.IO.Path.Combine(home, "sessions"));
        var databasePath = temporary.GetPath("usage.db");

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: databasePath,
                TimeZone: TimeZoneInfo.Utc));
        await service.InitializeAsync();
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(databasePath, "damaged after startup");

        var result = await service.RebuildIndexAsync();
        var snapshot = await service.QueryPeriodAsync(
            UsagePeriod.All,
            DateTimeOffset.UtcNow);

        Assert.True(result.CompletedSuccessfully);
        Assert.True(service.HasCompletedInitialIndex);
        Assert.Equal(0, snapshot.Summary.Total.TotalTokens);
        Assert.Single(Directory.GetFiles(
            temporary.Path,
            "usage.db.corrupt-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task OpenAsync_DoesNotQuarantineOrdinarySchemaErrors()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        Directory.CreateDirectory(System.IO.Path.Combine(home, "sessions"));
        var databasePath = temporary.GetPath("usage.db");
        await using (var connection = new SqliteConnection(
                         $"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE file_state(unrelated TEXT);";
            await command.ExecuteNonQueryAsync();
        }

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: databasePath,
                TimeZone: TimeZoneInfo.Utc));

        await Assert.ThrowsAsync<SqliteException>(() => service.OpenAsync());

        Assert.True(File.Exists(databasePath));
        Assert.Empty(Directory.GetFiles(
            temporary.Path,
            "usage.db.corrupt-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task OpenAsync_BoundsRetainedCorruptIndexBackups()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        Directory.CreateDirectory(System.IO.Path.Combine(home, "sessions"));
        var databasePath = temporary.GetPath("usage.db");
        await File.WriteAllTextAsync(databasePath, "currently corrupt");
        for (var index = 0; index < 4; index++)
        {
            var old = databasePath + $".corrupt-old-{index}";
            await File.WriteAllTextAsync(old, "old corrupt index");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10 - index));
        }

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: databasePath,
                TimeZone: TimeZoneInfo.Utc));

        await service.OpenAsync();

        Assert.Equal(2, Directory.GetFiles(
            temporary.Path,
            "usage.db.corrupt-*",
            SearchOption.TopDirectoryOnly).Length);
    }

    [Fact]
    public async Task RefreshAsync_DoesNotMarkInvalidSourceShapeComplete()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(System.IO.Path.Combine(
            home,
            "archived_sessions"));
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(home, "sessions"),
            "not a directory");

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        await service.OpenAsync();

        var result = await service.RefreshAsync();

        Assert.False(result.CompletedSuccessfully);
        Assert.False(service.HasCompletedInitialIndex);
        Assert.True(service.RequiresHistoryBuild);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndLateOperationsFailCleanly()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        Directory.CreateDirectory(System.IO.Path.Combine(home, "sessions"));
        var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: temporary.GetPath("usage.db"),
                TimeZone: TimeZoneInfo.Utc));
        await service.OpenAsync();

        await Task.WhenAll(
            service.DisposeAsync().AsTask(),
            service.DisposeAsync().AsTask());

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.RefreshAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.OpenAsync());
    }
}
