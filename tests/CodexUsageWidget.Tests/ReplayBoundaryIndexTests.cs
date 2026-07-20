using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class ReplayBoundaryIndexTests
{
    private const string RootId = "11111111-1111-1111-1111-111111111111";
    private const string ChildId = "22222222-2222-2222-2222-222222222222";
    private const string SiblingChildId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task ChildSingleBoundary_ExcludesReplayPrefixAndKeepsCumulativeBaseline()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = ChildPath(temporary, ChildId);
        var timestamp = Utc(2026, 7, 18, 1);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(ChildId, RootId, RootId, timestamp),
            TestLog.TokenCount(timestamp, 100, 60, 1, 10, 2),
            TestLog.TokenCount(timestamp.AddMinutes(1), 200, 120, 2, 20, 4),
            TestLog.ReplayBoundary(timestamp.AddMinutes(2)),
            TestLog.TokenCount(timestamp.AddMinutes(3), 250, 150, 3, 30, 6));

        await repository.IndexFileAsync(path, isArchived: false);
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.Equal(
            new TokenUsage(50, 30, 1, 10, 2),
            snapshot.Summary.Total);
        Assert.Equal(60, snapshot.Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task ChildBoundaryBeforeFirstToken_KeepsTheEntireFirstRealTurn()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = ChildPath(temporary, ChildId);
        var timestamp = Utc(2026, 7, 20, 1);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(ChildId, RootId, RootId, timestamp),
            // Some real child rollouts place the trigger marker before the
            // first token_count. That first cumulative value belongs to the
            // child's first model turn and must not be treated as replay.
            TestLog.ReplayBoundary(timestamp.AddSeconds(1)),
            TestLog.TokenCount(
                timestamp.AddSeconds(2),
                19_685,
                0,
                0,
                193,
                84),
            TestLog.TokenCount(
                timestamp.AddSeconds(3),
                39_708,
                18_944,
                0,
                281,
                106));

        await repository.IndexFileAsync(path, isArchived: false);
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.Equal(
            new TokenUsage(39_708, 18_944, 0, 281, 106),
            snapshot.Summary.Total);
        Assert.Equal(39_989, snapshot.Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task ChildMultipleBoundaries_OnlyFirstBoundaryEndsReplayPrefix()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = ChildPath(temporary, ChildId);
        var timestamp = Utc(2026, 7, 18, 1);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(ChildId, RootId, RootId, timestamp),
            TestLog.TokenCount(timestamp, 100, 60, 0, 10, 2),
            TestLog.ReplayBoundary(timestamp.AddMinutes(1)),
            TestLog.TokenCount(timestamp.AddMinutes(2), 140, 80, 1, 16, 4),
            // Later inter-agent messages are normal child work. They must not
            // create another replay prefix or erase already accepted usage.
            TestLog.ReplayBoundary(timestamp.AddMinutes(3)),
            TestLog.TokenCount(timestamp.AddMinutes(4), 180, 100, 2, 25, 7));

        await repository.IndexFileAsync(path, isArchived: false);
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.Equal(
            new TokenUsage(80, 40, 2, 15, 5),
            snapshot.Summary.Total);
        Assert.Equal(95, snapshot.Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task RootSessionBoundary_DoesNotExcludeRootHistory()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = temporary.GetPath($"rollout-{RootId}.jsonl");
        var timestamp = Utc(2026, 7, 18, 1);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId, null, timestamp),
            TestLog.TokenCount(timestamp, 100, 60, 1, 10, 2),
            TestLog.ReplayBoundary(timestamp.AddMinutes(1)),
            TestLog.TokenCount(timestamp.AddMinutes(2), 150, 90, 2, 20, 4));

        await repository.IndexFileAsync(path, isArchived: false);
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.Equal(
            new TokenUsage(150, 90, 2, 20, 4),
            snapshot.Summary.Total);
        Assert.Equal(170, snapshot.Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task ChildWithoutBoundary_KeepsEntireSession()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = ChildPath(temporary, ChildId);
        var timestamp = Utc(2026, 7, 18, 1);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(ChildId, RootId, RootId, timestamp),
            TestLog.TokenCount(timestamp, 100, 60, 1, 10, 2),
            TestLog.TokenCount(timestamp.AddMinutes(1), 150, 90, 2, 20, 4));

        await repository.IndexFileAsync(path, isArchived: false);
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.Equal(
            new TokenUsage(150, 90, 2, 20, 4),
            snapshot.Summary.Total);
    }

    [Fact]
    public async Task BoundaryDiscoveredIncrementally_RemovesPreviouslyIndexedPrefix()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = temporary.GetPath("usage.db");
        var repository = await CreateRepositoryAsync(databasePath);
        var path = ChildPath(temporary, ChildId);
        var timestamp = Utc(2026, 7, 18, 1);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(ChildId, RootId, RootId, timestamp),
            TestLog.TokenCount(timestamp, 100, 60, 1, 10, 2));
        await repository.IndexFileAsync(path, isArchived: false);

        var beforeBoundary = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));
        Assert.Equal(110, beforeBoundary.Summary.Total.TotalTokens);

        await TestLog.AppendLinesAsync(
            path,
            TestLog.ReplayBoundary(timestamp.AddMinutes(1)),
            TestLog.TokenCount(timestamp.AddMinutes(2), 150, 90, 2, 20, 4));
        await repository.IndexFileAsync(path, isArchived: false);

        var corrected = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));
        Assert.Equal(
            new TokenUsage(50, 30, 1, 10, 2),
            corrected.Summary.Total);

        // Reopening, initializing, and indexing again must not subtract the
        // prefix twice or add the accepted suffix twice.
        var reopened = await CreateRepositoryAsync(databasePath);
        await reopened.InitializeAsync();
        await reopened.IndexFileAsync(path, isArchived: false);
        var repeated = await reopened.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));
        Assert.Equal(corrected.Summary.Total, repeated.Summary.Total);
    }

    [Fact]
    public async Task ReplayCleanup_KeepsDailyAndAllPeriodAggregatesConsistent()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = ChildPath(temporary, ChildId);
        var firstDay = Utc(2026, 7, 18, 1);
        var secondDay = Utc(2026, 7, 19, 1);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(ChildId, RootId, RootId, firstDay),
            TestLog.TokenCount(firstDay, 100, 60, 1, 10, 2),
            TestLog.ReplayBoundary(firstDay.AddMinutes(1)),
            TestLog.TokenCount(firstDay.AddMinutes(2), 130, 75, 2, 15, 3),
            TestLog.TokenCount(secondDay, 180, 100, 3, 25, 5));

        await repository.IndexFileAsync(path, isArchived: false);

        var all = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            secondDay.AddHours(1));
        var today = await repository.QueryPeriodAsync(
            UsagePeriod.Today,
            secondDay.AddHours(1));
        var week = await repository.QueryPeriodAsync(
            UsagePeriod.Last7Days,
            secondDay.AddHours(1));

        Assert.Equal(
            new TokenUsage(80, 40, 2, 15, 3),
            all.Summary.Total);
        Assert.Equal(
            new TokenUsage(50, 25, 1, 10, 2),
            today.Summary.Total);
        Assert.Equal(all.Summary.Total, week.Summary.Total);
        Assert.Equal(
            all.Summary.Total,
            new TokenUsage(30, 15, 1, 5, 1) + today.Summary.Total);
    }

    [Fact]
    public async Task ReplayCleanup_RebuildsSharedRootDateWithoutDroppingOtherFiles()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var timestamp = Utc(2026, 7, 18, 1);
        var rootPath = temporary.GetPath($"rollout-root-{RootId}.jsonl");
        var targetChildPath = ChildPath(temporary, ChildId);
        var siblingChildPath = ChildPath(temporary, SiblingChildId);

        await TestLog.WriteLinesAsync(
            rootPath,
            TestLog.SessionMeta(RootId, RootId, null, timestamp),
            TestLog.TokenCount(timestamp, 300, 180, 3, 30, 6));
        await TestLog.WriteLinesAsync(
            siblingChildPath,
            TestLog.SessionMeta(
                SiblingChildId,
                RootId,
                RootId,
                timestamp.AddMinutes(1)),
            TestLog.TokenCount(
                timestamp.AddMinutes(1),
                25,
                10,
                0,
                5,
                1));
        await TestLog.WriteLinesAsync(
            targetChildPath,
            TestLog.SessionMeta(
                ChildId,
                RootId,
                RootId,
                timestamp.AddMinutes(2)),
            TestLog.TokenCount(
                timestamp.AddMinutes(2),
                100,
                60,
                1,
                10,
                2));

        await repository.IndexFileAsync(rootPath, isArchived: false);
        await repository.IndexFileAsync(siblingChildPath, isArchived: false);
        await repository.IndexFileAsync(targetChildPath, isArchived: false);

        var beforeBoundary = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));
        Assert.Equal(
            new TokenUsage(425, 250, 4, 45, 9),
            beforeBoundary.Summary.Total);

        await TestLog.AppendLinesAsync(
            targetChildPath,
            TestLog.ReplayBoundary(timestamp.AddMinutes(3)),
            TestLog.TokenCount(
                timestamp.AddMinutes(4),
                150,
                90,
                2,
                20,
                4));
        await repository.IndexFileAsync(
            targetChildPath,
            isArchived: false);

        var corrected = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.Equal(
            new TokenUsage(375, 220, 4, 45, 9),
            corrected.Summary.Total);
        Assert.Equal(420, corrected.Summary.Total.TotalTokens);
        Assert.Equal(1, corrected.Summary.TaskCount);
        Assert.Single(corrected.TopTasks);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private static string ChildPath(
        TemporaryDirectory temporary,
        string childId) =>
        temporary.GetPath($"rollout-child-{childId}.jsonl");

    private static Task<UsageRepository> CreateRepositoryAsync(
        TemporaryDirectory temporary) =>
        CreateRepositoryAsync(temporary.GetPath("usage.db"));

    private static async Task<UsageRepository> CreateRepositoryAsync(
        string databasePath)
    {
        var repository = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await repository.InitializeAsync();
        return repository;
    }
}
