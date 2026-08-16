using CodexUsageWidget.Core;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CodexUsageWidget.Tests;

public sealed class RealLogAuditTests
{
    private readonly ITestOutputHelper _output;

    public RealLogAuditTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task RealCodexLogs_ReadOnlyAudit_WhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CODEX_USAGE_REAL_LOG_AUDIT"),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                "Set CODEX_USAGE_REAL_LOG_AUDIT=1 to run the 2+ GB read-only audit.");
            return;
        }

        var codexHome = GetRealCodexHome();
        if (string.IsNullOrWhiteSpace(codexHome)
            || !Directory.Exists(codexHome))
        {
            _output.WriteLine("Real Codex Home is not present on this machine.");
            return;
        }

        using var temporary = new TemporaryDirectory();
        var chinaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: codexHome,
                DatabasePath: temporary.GetPath("real-audit.db"),
                TimeZone: chinaTimeZone));

        await service.InitializeAsync();
        var now = DateTimeOffset.Now;
        var today = await service.QueryPeriodAsync(UsagePeriod.Today, now);
        var week = await service.QueryPeriodAsync(UsagePeriod.Last7Days, now);
        var month = await service.QueryPeriodAsync(UsagePeriod.Month, now);
        var all = await service.QueryPeriodAsync(UsagePeriod.All, now);

        AssertSnapshotInvariant(today);
        AssertSnapshotInvariant(week);
        AssertSnapshotInvariant(month);
        AssertSnapshotInvariant(all);
        Assert.True(all.Summary.Total.TotalTokens >= month.Summary.Total.TotalTokens);
        Assert.True(month.Summary.Total.TotalTokens >= today.Summary.Total.TotalTokens);
        Assert.True(all.Summary.TaskCount > 0);
        Assert.NotNull(all.RateLimits);

        _output.WriteLine(
            "today={0:N0}; week={1:N0}; month={2:N0}; all={3:N0}; tasks={4}; remaining={5}",
            today.Summary.Total.TotalTokens,
            week.Summary.Total.TotalTokens,
            month.Summary.Total.TotalTokens,
            all.Summary.Total.TotalTokens,
            all.Summary.TaskCount,
            all.RateLimits?.RemainingPercent);
    }

    [Fact]
    public async Task RealCodexLogs_FastStartupAudit_WhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CODEX_USAGE_REAL_FAST_AUDIT"),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                "Set CODEX_USAGE_REAL_FAST_AUDIT=1 to run the read-only fast-start audit.");
            return;
        }

        var codexHome = GetRealCodexHome();
        if (string.IsNullOrWhiteSpace(codexHome)
            || !Directory.Exists(codexHome))
        {
            _output.WriteLine("Real Codex Home is not present on this machine.");
            return;
        }

        using var temporary = new TemporaryDirectory();
        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: codexHome,
                DatabasePath: temporary.GetPath("fast-start.db"),
                TimeZone: TimeZoneInfo.Local));

        var stopwatch = Stopwatch.StartNew();
        await service.OpenAsync();
        var indexOpenElapsed = stopwatch.Elapsed;
        var rateLimits = await service.ReadLatestRateLimitsFromRecentLogsAsync();
        stopwatch.Stop();

        Assert.NotNull(rateLimits);
        Assert.Empty(
            (await service.QueryPeriodAsync(UsagePeriod.All)).TopTasks);
        _output.WriteLine(
            "open_ms={0:N1}; open_plus_tail_ms={1:N1}; remaining={2}",
            indexOpenElapsed.TotalMilliseconds,
            stopwatch.Elapsed.TotalMilliseconds,
            rateLimits.RemainingPercent);
    }

    [Fact]
    public async Task RealIndexedDatabase_RefreshAndQueryTiming_WhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "CODEX_USAGE_REAL_INDEX_AUDIT"),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                "Set CODEX_USAGE_REAL_INDEX_AUDIT=1 to time the live incremental index.");
            return;
        }

        var codexHome = GetRealCodexHome();
        if (string.IsNullOrWhiteSpace(codexHome)
            || !Directory.Exists(codexHome))
        {
            _output.WriteLine("Real Codex Home is not present on this machine.");
            return;
        }
        var appData = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "CodexUsageWidget");
        var databasePath = UsageIndexDatabasePath.ForHome(
            appData,
            codexHome);
        Assert.True(
            File.Exists(databasePath),
            $"The active Codex Home index does not exist: {databasePath}");

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: codexHome,
                DatabasePath: databasePath,
                TimeZone: TimeZoneInfo.Local));

        var stopwatch = Stopwatch.StartNew();
        await service.OpenAsync();
        var openElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        var refresh = await service.RefreshAsync();
        var refreshElapsed = stopwatch.Elapsed;

        var timings = new List<string>();
        var taskCounts = new List<string>();
        foreach (var period in new[]
                 {
                     UsagePeriod.Today,
                     UsagePeriod.Last7Days,
                     UsagePeriod.Month,
                     UsagePeriod.All,
                 })
        {
            stopwatch.Restart();
            var snapshot = await service.QueryPeriodAsync(period);
            stopwatch.Stop();
            AssertSnapshotInvariant(snapshot);
            timings.Add(
                $"{period}={stopwatch.Elapsed.TotalMilliseconds:N1}ms");
            taskCounts.Add($"{period}={snapshot.Summary.TaskCount}");
        }

        var now = DateTimeOffset.Now;
        var weeklyHistory = await service.QueryWeeklyRateLimitDailyUsageAsync(
            now.AddDays(-8),
            now.AddDays(1));

        _output.WriteLine(
            "open={0:N1}ms; refresh={1:N1}ms; changed_files={2}; changed_bytes={3:N0}; {4}; tasks[{5}]; weekly_days={6}",
            openElapsed.TotalMilliseconds,
            refreshElapsed.TotalMilliseconds,
            refresh.FilesChanged,
            refresh.BytesProcessed,
            string.Join("; ", timings),
            string.Join("; ", taskCounts),
            weeklyHistory.Count);
    }

    private static string? GetRealCodexHome() =>
        Environment.GetEnvironmentVariable("CODEX_USAGE_REAL_CODEX_HOME")
        ?? Environment.GetEnvironmentVariable("CODEX_HOME");

    private static void AssertSnapshotInvariant(PeriodSnapshot snapshot)
    {
        Assert.Equal(
            snapshot.Summary.Total,
            snapshot.Summary.TopTasksTotal + snapshot.Summary.OtherTasksTotal);
        Assert.Equal(
            snapshot.Summary.Total.InputTokens +
            snapshot.Summary.Total.OutputTokens,
            snapshot.Summary.Total.TotalTokens);
        Assert.All(
            snapshot.TopTasks,
            static task => Assert.True(task.Usage.TotalTokens >= 0));
    }
}
