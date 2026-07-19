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
        await TestLog.WriteLinesAsync(
            System.IO.Path.Combine(home, "session_index.jsonl"),
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
        Assert.Equal(1d, service.IndexProgress);
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
        Assert.InRange(progressEvents.Count, 2, 4);
        Assert.True(progressEvents[^1].IsComplete);
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
}
