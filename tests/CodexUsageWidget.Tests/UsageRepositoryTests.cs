using CodexUsageWidget.Core;
using Microsoft.Data.Sqlite;

namespace CodexUsageWidget.Tests;

public sealed class UsageRepositoryTests
{
    private const string RootId = "11111111-1111-1111-1111-111111111111";
    private static readonly double[] ExpectedDailyWeeklyConsumption =
        [18d, 33d, 25d, 49d, 27d, 61d, 6d];
    private static readonly double[] ExpectedDailyWeeklyClosingUsage =
        [29d, 33d, 25d, 74d, 27d, 88d, 94d];

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueryPeriod_RejectsUnsafeDateTimeBoundary(bool minimum)
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var unsafeTimestamp = minimum
            ? DateTimeOffset.MinValue
            : DateTimeOffset.MaxValue;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.QueryPeriodAsync(UsagePeriod.Last7Days, unsafeTimestamp));
    }

    [Fact]
    public async Task QueryPeriod_RejectsUnboundedTopTaskAllocation()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.QueryPeriodAsync(
                UsagePeriod.All,
                topTaskCount: int.MaxValue));
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_RejectsUnsafeDateTimeBoundary()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.QueryWeeklyRateLimitDailyUsageAsync(
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue.AddDays(1)));
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_RejectsUnboundedDateRange()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var from = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.QueryWeeklyRateLimitDailyUsageAsync(
                from,
                from.AddDays(367)));
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
    public async Task IndexFile_PersistsHighWaterAcrossIncrementalRollback()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = temporary.GetPath("usage.db");
        var logPath = temporary.GetPath($"rollout-jitter-{RootId}.jsonl");
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
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 603_764_545, 500_000_000, 0, 1_000, 100));

        var firstRepository = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await firstRepository.InitializeAsync();
        await firstRepository.IndexFileAsync(logPath, false);
        await TestLog.AppendLinesAsync(
            logPath,
            TestLog.TokenCount(
                timestamp.AddSeconds(1),
                603_754_655,
                499_999_000,
                0,
                999,
                99));
        var rollback = await firstRepository.IndexFileAsync(logPath, false);

        var reopened = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await reopened.InitializeAsync();
        await TestLog.AppendLinesAsync(
            logPath,
            TestLog.TokenCount(
                timestamp.AddSeconds(2),
                603_800_000,
                500_020_000,
                0,
                1_100,
                110));
        var increase = await reopened.IndexFileAsync(logPath, false);
        var snapshot = await reopened.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.Equal(0, rollback.InsertedEvents);
        Assert.Equal(1, increase.InsertedEvents);
        Assert.Equal(
            new TokenUsage(603_800_000, 500_020_000, 0, 1_100, 110),
            snapshot.Summary.Total);
    }

    [Fact]
    public async Task IndexFile_FirstChildTriggerRemovesPreviouslyIndexedReplayPrefix()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        const string childId = "22222222-2222-2222-2222-222222222222";
        var logPath = temporary.GetPath($"rollout-{childId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(childId, RootId, RootId),
            TestLog.TokenCount(timestamp, 100, 80, 0, 10, 5),
            TestLog.TokenCount(timestamp.AddSeconds(1), 200, 160, 0, 20, 10));
        await repository.IndexFileAsync(logPath, false);
        var beforeBoundary = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp);

        await TestLog.AppendLinesAsync(
            logPath,
            TestLog.ReplayBoundary(timestamp.AddSeconds(2)),
            TestLog.TokenCount(
                timestamp.AddSeconds(3),
                250,
                200,
                0,
                25,
                12));
        var boundaryResult = await repository.IndexFileAsync(logPath, false);
        var afterBoundary = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp);

        await TestLog.AppendLinesAsync(
            logPath,
            TestLog.ReplayBoundary(timestamp.AddSeconds(4)),
            TestLog.TokenCount(
                timestamp.AddSeconds(5),
                300,
                240,
                0,
                30,
                15));
        await repository.IndexFileAsync(logPath, false);
        var afterSecondTrigger = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp);

        Assert.Equal(220, beforeBoundary.Summary.Total.TotalTokens);
        Assert.Equal(1, boundaryResult.InsertedEvents);
        Assert.Equal(
            new TokenUsage(50, 40, 0, 5, 2),
            afterBoundary.Summary.Total);
        Assert.Equal(
            new TokenUsage(100, 80, 0, 10, 5),
            afterSecondTrigger.Summary.Total);
    }

    [Fact]
    public async Task IndexFile_MigratesLegacyChildReplayPrefixWithoutFullRescan()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = temporary.GetPath("usage.db");
        var repository = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await repository.InitializeAsync();
        const string childId = "22222222-2222-2222-2222-222222222222";
        var logPath = temporary.GetPath($"rollout-{childId}.jsonl");
        var sourceKey = UsageRepository.GetSourceKey(logPath);
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(childId, RootId, RootId),
            TestLog.TokenCount(timestamp, 100, 80, 0, 10, 5),
            TestLog.TokenCount(timestamp.AddSeconds(1), 200, 160, 0, 20, 10));
        await repository.IndexFileAsync(logPath, false);

        await TestLog.AppendLinesAsync(
            logPath,
            TestLog.ReplayBoundary(timestamp.AddSeconds(2)),
            TestLog.TokenCount(
                timestamp.AddSeconds(3),
                250,
                200,
                0,
                25,
                12));
        var parsed = await new CodexLogParser().ParseFileAsync(logPath, sourceKey);
        var postBoundary = Assert.Single(parsed.Deltas);
        var checkpointHash = await SharedFileAccess.ComputeCheckpointHashAsync(
            logPath,
            parsed.Checkpoint.Offset);
        var file = new FileInfo(logPath);

        await using (var connection = new SqliteConnection(
                         $"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT INTO token_events(
                        file_key,
                        event_offset,
                        timestamp_utc,
                        local_date,
                        root_task_id,
                        input_tokens,
                        cached_input_tokens,
                        cache_write_input_tokens,
                        output_tokens,
                        reasoning_output_tokens)
                    VALUES(
                        $key,
                        $offset,
                        $timestamp,
                        $date,
                        $root,
                        $input,
                        $cached,
                        $cache_write,
                        $output,
                        $reasoning);
                    """;
                insert.Parameters.AddWithValue("$key", sourceKey);
                insert.Parameters.AddWithValue("$offset", postBoundary.EventOffset);
                insert.Parameters.AddWithValue(
                    "$timestamp",
                    postBoundary.Timestamp.UtcDateTime.ToString("O"));
                insert.Parameters.AddWithValue("$date", "2026-07-18");
                insert.Parameters.AddWithValue("$root", RootId);
                insert.Parameters.AddWithValue(
                    "$input",
                    postBoundary.Usage.InputTokens);
                insert.Parameters.AddWithValue(
                    "$cached",
                    postBoundary.Usage.CachedInputTokens);
                insert.Parameters.AddWithValue(
                    "$cache_write",
                    postBoundary.Usage.CacheWriteInputTokens);
                insert.Parameters.AddWithValue(
                    "$output",
                    postBoundary.Usage.OutputTokens);
                insert.Parameters.AddWithValue(
                    "$reasoning",
                    postBoundary.Usage.ReasoningOutputTokens);
                await insert.ExecuteNonQueryAsync();
            }

            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE file_state
                SET file_length = $length,
                    processed_offset = $processed,
                    last_write_utc_ticks = $write_ticks,
                    previous_input = 250,
                    previous_cached_input = 200,
                    previous_cache_write_input = 0,
                    previous_output = 25,
                    previous_reasoning_output = 12,
                    replay_boundary_seen = 1,
                    first_replay_boundary_offset = NULL,
                    checkpoint_hash = $hash
                WHERE file_key = $key;
                """;
            update.Parameters.AddWithValue("$length", file.Length);
            update.Parameters.AddWithValue(
                "$processed",
                parsed.Checkpoint.Offset);
            update.Parameters.AddWithValue(
                "$write_ticks",
                file.LastWriteTimeUtc.Ticks);
            update.Parameters.AddWithValue("$hash", checkpointHash);
            update.Parameters.AddWithValue("$key", sourceKey);
            await update.ExecuteNonQueryAsync();
        }

        var migratedRepository = new UsageRepository(
            databasePath,
            TimeZoneInfo.Utc);
        await migratedRepository.InitializeAsync();
        await migratedRepository.IndexFileAsync(logPath, false);
        var snapshot = await migratedRepository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp);

        Assert.Equal(
            new TokenUsage(50, 40, 0, 5, 2),
            snapshot.Summary.Total);
        Assert.Equal(
            parsed.Checkpoint.Offset,
            await migratedRepository.GetIndexedOffsetAsync(logPath));
    }

    [Fact]
    public async Task IndexFile_VersionTwoReparsesOnlyRootLikeFork()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = temporary.GetPath("usage.db");
        var repository = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await repository.InitializeAsync();
        const string forkId = "33333333-3333-3333-3333-333333333333";
        const string normalId = "44444444-4444-4444-4444-444444444444";
        var parentPath = temporary.GetPath($"rollout-parent-{RootId}.jsonl");
        var forkPath = temporary.GetPath($"rollout-{forkId}.jsonl");
        var normalPath = temporary.GetPath($"rollout-{normalId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 28, 20, 25, 39, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            parentPath,
            TestLog.SessionMeta(RootId, RootId, timestamp: timestamp),
            TestLog.TokenCount(timestamp.AddSeconds(1), 100, 80, 0, 10, 5),
            TestLog.ReplayBoundary(timestamp.AddSeconds(2)),
            TestLog.TokenCount(timestamp.AddSeconds(3), 200, 160, 0, 20, 10),
            TestLog.TokenCount(timestamp.AddSeconds(4), 300, 240, 0, 30, 15));
        // Simulate the v2 classification: session_id points to itself, so the
        // copied prefix was indexed as ordinary root usage.
        await TestLog.WriteLinesAsync(
            forkPath,
            TestLog.SessionMeta(forkId, forkId, timestamp: timestamp),
            TestLog.TokenCount(timestamp.AddSeconds(1), 100, 80, 0, 10, 5),
            TestLog.ReplayBoundary(timestamp.AddSeconds(2)),
            TestLog.TokenCount(timestamp.AddSeconds(3), 200, 160, 0, 20, 10),
            TestLog.TokenCount(timestamp.AddSeconds(5), 250, 200, 0, 25, 12));
        await TestLog.WriteLinesAsync(
            normalPath,
            TestLog.SessionMeta(normalId, normalId, timestamp: timestamp),
            TestLog.TokenCount(timestamp.AddSeconds(1), 30, 20, 0, 3, 1));
        await repository.IndexFileAsync(parentPath, false);
        await repository.IndexFileAsync(forkPath, false);
        await repository.IndexFileAsync(normalPath, false);

        // The real metadata was present in the affected rollout. Rewrite it
        // here after creating the legacy state so the migration sees exactly
        // the root-like subagent fork shape found in production.
        await TestLog.WriteLinesAsync(
            forkPath,
            TestLog.SessionMeta(
                forkId,
                forkId,
                timestamp: timestamp,
                forkedFromId: RootId,
                threadSource: "subagent"),
            TestLog.TokenCount(timestamp.AddSeconds(1), 100, 80, 0, 10, 5),
            TestLog.ReplayBoundary(timestamp.AddSeconds(2)),
            TestLog.TokenCount(timestamp.AddSeconds(3), 200, 160, 0, 20, 10),
            TestLog.TokenCount(timestamp.AddSeconds(5), 250, 200, 0, 25, 12));

        await using (var connection = new SqliteConnection(
                         $"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            foreach (var path in new[] { forkPath, normalPath })
            {
                var file = new FileInfo(path);
                var key = UsageRepository.GetSourceKey(path);
                var hash = await SharedFileAccess.ComputeCheckpointHashAsync(
                    path,
                    file.Length);
                await using var update = connection.CreateCommand();
                update.CommandText =
                    """
                    UPDATE file_state
                    SET file_length = $length,
                        processed_offset = $length,
                        last_write_utc_ticks = $ticks,
                        token_accounting_version = 2,
                        checkpoint_hash = $hash
                    WHERE file_key = $key;
                    """;
                update.Parameters.AddWithValue("$length", file.Length);
                update.Parameters.AddWithValue("$ticks", file.LastWriteTimeUtc.Ticks);
                update.Parameters.AddWithValue("$hash", hash);
                update.Parameters.AddWithValue("$key", key);
                await update.ExecuteNonQueryAsync();
            }
        }

        var reopened = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await reopened.InitializeAsync();
        var pendingMigration = await reopened.LoadIndexedFileMetadataAsync();
        Assert.False(
            pendingMigration[UsageRepository.GetSourceKey(parentPath)]
                .NeedsForkReplayMigration);
        Assert.True(
            pendingMigration[UsageRepository.GetSourceKey(forkPath)]
                .NeedsForkReplayMigration);
        Assert.True(
            pendingMigration[UsageRepository.GetSourceKey(normalPath)]
                .NeedsForkReplayMigration);
        var normalMigration = await reopened.IndexFileAsync(normalPath, false);
        var forkMigration = await reopened.IndexFileAsync(forkPath, false);
        var snapshot = await reopened.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.False(normalMigration.WasReset);
        Assert.Equal(0, normalMigration.InsertedEvents);
        Assert.True(forkMigration.WasReset);
        Assert.Equal(1, forkMigration.InsertedEvents);
        Assert.Equal(new TokenUsage(380, 300, 0, 38, 18), snapshot.Summary.Total);
    }

    [Fact]
    public async Task IndexFile_ForkWaitsUntilItsParentHasBeenIndexed()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        const string forkId = "55555555-5555-5555-5555-555555555555";
        var parentPath = temporary.GetPath($"rollout-parent-{RootId}.jsonl");
        var forkPath = temporary.GetPath($"rollout-fork-{forkId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 28, 21, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            parentPath,
            TestLog.SessionMeta(RootId, RootId, timestamp: timestamp),
            TestLog.TokenCount(timestamp.AddSeconds(1), 200, 160, 0, 20, 10));
        await TestLog.WriteLinesAsync(
            forkPath,
            TestLog.SessionMeta(
                forkId,
                forkId,
                timestamp: timestamp,
                forkedFromId: RootId,
                threadSource: "subagent"),
            TestLog.TokenCount(timestamp.AddSeconds(1), 200, 160, 0, 20, 10),
            TestLog.TokenCount(timestamp.AddSeconds(2), 250, 200, 0, 25, 12));

        var pending = await repository.IndexFileAsync(forkPath, false);
        var beforeParent = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));
        await repository.IndexFileAsync(parentPath, false);
        var indexed = await repository.IndexFileAsync(forkPath, false);
        var afterParent = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.True(pending.NeedsReplayMigration);
        Assert.Equal(0, pending.CurrentOffset);
        Assert.Equal(TokenUsage.Zero, beforeParent.Summary.Total);
        Assert.False(indexed.NeedsReplayMigration);
        Assert.Equal(1, indexed.InsertedEvents);
        Assert.Equal(
            new TokenUsage(250, 200, 0, 25, 12),
            afterParent.Summary.Total);
    }

    [Fact]
    public async Task IndexFile_OrdinaryChildForkKeepsLightweightMarkerTrim()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        const string childId = "66666666-6666-6666-6666-666666666666";
        var childPath = temporary.GetPath($"rollout-child-{childId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 28, 22, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            childPath,
            TestLog.SessionMeta(
                childId,
                RootId,
                parentThreadId: RootId,
                timestamp: timestamp,
                forkedFromId: RootId,
                threadSource: "subagent"),
            TestLog.TokenCount(timestamp.AddSeconds(1), 200, 160, 0, 20, 10),
            TestLog.ReplayBoundary(timestamp.AddSeconds(2)),
            TestLog.TokenCount(timestamp.AddSeconds(3), 250, 200, 0, 25, 12));

        var indexed = await repository.IndexFileAsync(childPath, false);
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.False(indexed.NeedsReplayMigration);
        Assert.Equal(1, indexed.InsertedEvents);
        Assert.Equal(
            new TokenUsage(50, 40, 0, 5, 2),
            snapshot.Summary.Total);
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
                new DateTimeOffset(2026, 7, 19, 10, 1, 0, TimeSpan.Zero),
                12d,
                sparseReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 11, 1, 0, TimeSpan.Zero),
                14d,
                sparseReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 12, 1, 0, TimeSpan.Zero),
                16d,
                sparseReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 13, 1, 0, TimeSpan.Zero),
                18d,
                sparseReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 9, 30, 0, TimeSpan.Zero),
                70d,
                sparseReset),
            // This row is chronologically latest but is a stale rollback on
            // the sparse timeline. It must not make that timeline authoritative.
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 11, 0, 0, TimeSpan.Zero),
                60d,
                sparseReset));

        await repository.IndexFileAsync(canonicalPath, false);
        await repository.IndexFileAsync(sparsePath, false);

        var day = Assert.Single(
            await repository.QueryWeeklyRateLimitDailyUsageAsync(
                new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(20d, day.ConsumedPercentagePoints!.Value, 6);
        Assert.Equal(50d, day.LastObservedUsedPercent);
        // The sparse schedule has more total samples, but its last observation
        // is earlier. The day's final valid timeline remains authoritative.
        Assert.False(day.IsPartial);
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
    public async Task QueryWeeklyRateLimitDailyUsage_IgnoresWindowThatHasNotStarted()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var path = temporary.GetPath("rollout-future-window.jsonl");
        var validReset =
            new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var notStartedReset =
            new DateTimeOffset(2026, 7, 28, 11, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(
                "71000000-0000-0000-0000-000000000007",
                "71000000-0000-0000-0000-000000000007"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
                30d,
                validReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
                40d,
                validReset),
            // Although chronologically last, this impossible snapshot names a
            // seven-day window whose start is still one day in the future.
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 11, 0, 0, TimeSpan.Zero),
                90d,
                notStartedReset));
        await repository.IndexFileAsync(path, false);

        var day = Assert.Single(
            await repository.QueryWeeklyRateLimitDailyUsageAsync(
                new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(40d, day.ConsumedPercentagePoints!.Value, 6);
        Assert.Equal(40d, day.LastObservedUsedPercent);
        Assert.False(day.IsPartial);
    }

    [Fact]
    public async Task QueryWeeklyRateLimitDailyUsage_UsesEachDaysLatestValidTimeline()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary);
        var timelineAReset =
            new DateTimeOffset(2026, 7, 20, 2, 12, 53, TimeSpan.Zero);
        var timelineBReset =
            new DateTimeOffset(2026, 7, 22, 2, 45, 56, TimeSpan.Zero);
        var timelineCReset =
            new DateTimeOffset(2026, 7, 23, 4, 58, 47, TimeSpan.Zero);
        var timelineDReset =
            new DateTimeOffset(2026, 7, 25, 3, 48, 32, TimeSpan.Zero);
        var timelineDReset33 = timelineDReset.AddSeconds(1);
        var timelineDReset35 = timelineDReset.AddSeconds(3);

        var timelineAPath = temporary.GetPath("rollout-timeline-a.jsonl");
        var timelineBPath = temporary.GetPath("rollout-timeline-b.jsonl");
        var timelineCPath = temporary.GetPath("rollout-timeline-c.jsonl");
        var timelineDPath = temporary.GetPath("rollout-timeline-d.jsonl");

        await TestLog.WriteLinesAsync(
            timelineAPath,
            TestLog.SessionMeta(
                "81000000-0000-0000-0000-000000000001",
                "81000000-0000-0000-0000-000000000001"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 13, 23, 0, 0, TimeSpan.Zero),
                11d,
                timelineAReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
                20d,
                timelineAReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 14, 23, 59, 0, TimeSpan.Zero),
                29d,
                timelineAReset),
            // Still valid, but replaced by timeline B later on July 15.
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero),
                30d,
                timelineAReset));
        await TestLog.WriteLinesAsync(
            timelineBPath,
            TestLog.SessionMeta(
                "82000000-0000-0000-0000-000000000002",
                "82000000-0000-0000-0000-000000000002"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 15, 2, 46, 0, TimeSpan.Zero),
                0d,
                timelineBReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
                20d,
                timelineBReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 15, 23, 59, 59, TimeSpan.Zero),
                33d,
                timelineBReset),
            // More samples and a higher value must not override the later
            // timeline C observation that closes July 16.
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero),
                49d,
                timelineBReset));
        await TestLog.WriteLinesAsync(
            timelineCPath,
            TestLog.SessionMeta(
                "83000000-0000-0000-0000-000000000003",
                "83000000-0000-0000-0000-000000000003"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 16, 5, 1, 0, TimeSpan.Zero),
                0d,
                timelineCReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
                10d,
                timelineCReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 16, 23, 43, 0, TimeSpan.Zero),
                25d,
                timelineCReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 17, 0, 1, 0, TimeSpan.Zero),
                25d,
                timelineCReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero),
                50d,
                timelineCReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 17, 23, 22, 0, TimeSpan.Zero),
                74d,
                timelineCReset));
        await TestLog.WriteLinesAsync(
            timelineDPath,
            TestLog.SessionMeta(
                "84000000-0000-0000-0000-000000000004",
                "84000000-0000-0000-0000-000000000004"),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 18, 3, 49, 0, TimeSpan.Zero),
                0d,
                timelineDReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero),
                10d,
                timelineDReset33),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 18, 23, 59, 0, TimeSpan.Zero),
                27d,
                timelineDReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 0, 0, 2, TimeSpan.Zero),
                27d,
                timelineDReset35),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero),
                60d,
                timelineDReset33),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 19, 23, 58, 0, TimeSpan.Zero),
                88d,
                timelineDReset),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 0, 0, 14, TimeSpan.Zero),
                88d,
                timelineDReset35),
            TestLog.WeeklyRateLimit(
                new DateTimeOffset(2026, 7, 20, 11, 46, 0, TimeSpan.Zero),
                94d,
                timelineDReset));

        await repository.IndexFileAsync(timelineAPath, false);
        await repository.IndexFileAsync(timelineBPath, false);
        await repository.IndexFileAsync(timelineCPath, false);
        await repository.IndexFileAsync(timelineDPath, false);

        var history = await repository.QueryWeeklyRateLimitDailyUsageAsync(
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            ExpectedDailyWeeklyConsumption,
            history.Select(
                static day => day.ConsumedPercentagePoints!.Value));
        Assert.Equal(
            ExpectedDailyWeeklyClosingUsage,
            history.Select(
                static day => day.LastObservedUsedPercent!.Value));
        Assert.All(history, static day => Assert.False(day.IsPartial));
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

    [Fact]
    public async Task TimeZoneIdentityChange_ResetsDerivedIndexAndCompletionMarker()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = temporary.GetPath("usage-time-zone.db");
        var logPath = temporary.GetPath($"rollout-zone-{RootId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 18, 0, 0, TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 100, 50, 0, 20, 5));
        const string sharedZoneId = "CodexUsageWidget.Tests.SameId";
        var zoneA = TimeZoneInfo.CreateCustomTimeZone(
            sharedZoneId,
            TimeSpan.Zero,
            "Zone A",
            "Zone A");
        var zoneB = TimeZoneInfo.CreateCustomTimeZone(
            sharedZoneId,
            TimeSpan.FromHours(12),
            "Zone B",
            "Zone B");

        var first = new UsageRepository(databasePath, zoneA);
        await first.InitializeAsync();
        Assert.False(await first.EnsureTimeZoneCompatibilityAsync());
        await first.IndexFileAsync(logPath, false);
        await first.MarkRefreshCompleteAsync(timestamp);

        var reopened = new UsageRepository(databasePath, zoneB);
        await reopened.InitializeAsync();
        Assert.True(await reopened.EnsureTimeZoneCompatibilityAsync());

        Assert.False(await reopened.HasCompletedInitialIndexAsync());
        Assert.Equal(
            TokenUsage.Zero,
            (await reopened.QueryPeriodAsync(UsagePeriod.All, timestamp))
                .Summary.Total);
    }

    [Fact]
    public async Task LegacyIndex_TimeZoneMismatchIsDetectedFromEventDates()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = temporary.GetPath("usage-legacy-zone.db");
        var logPath = temporary.GetPath($"rollout-legacy-zone-{RootId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 18, 0, 0, TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 100, 50, 0, 20, 5));
        var original = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await original.InitializeAsync();
        await original.IndexFileAsync(logPath, false);

        var shiftedZone = TimeZoneInfo.CreateCustomTimeZone(
            "CodexUsageWidget.Tests.LegacyShifted",
            TimeSpan.FromHours(12),
            "Shifted",
            "Shifted");
        var reopened = new UsageRepository(databasePath, shiftedZone);
        await reopened.InitializeAsync();

        Assert.True(await reopened.EnsureTimeZoneCompatibilityAsync());
        Assert.Equal(
            TokenUsage.Zero,
            (await reopened.QueryPeriodAsync(UsagePeriod.All, timestamp))
                .Summary.Total);
    }

    [Fact]
    public async Task LegacyIndex_MatchingTimeZoneIsAdoptedWithoutDataLoss()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = temporary.GetPath("usage-legacy-same-zone.db");
        var logPath = temporary.GetPath($"rollout-legacy-same-{RootId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 18, 0, 0, TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 100, 50, 0, 20, 5));
        var original = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await original.InitializeAsync();
        await original.IndexFileAsync(logPath, false);

        var reopened = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await reopened.InitializeAsync();

        Assert.False(await reopened.EnsureTimeZoneCompatibilityAsync());
        Assert.Equal(
              120,
              (await reopened.QueryPeriodAsync(UsagePeriod.All, timestamp))
                  .Summary.Total.TotalTokens);
    }

    [Fact]
    public async Task LoadIndexedFileMetadata_OversizedVersionResetsDerivedCache()
    {
        await AssertSemanticMetadataDamageResetsCacheAsync(
            "token_accounting_version",
            long.MaxValue);
    }

    [Fact]
    public async Task LoadIndexedFileMetadata_OutOfRangeOffsetResetsDerivedCache()
    {
        await AssertSemanticMetadataDamageResetsCacheAsync(
            "processed_offset",
            -1L);
    }

    [Fact]
    public async Task LoadIndexedFileMetadata_WrongStorageTypeResetsDerivedCache()
    {
        await AssertSemanticMetadataDamageResetsCacheAsync(
            "path",
            new byte[] { 0x01, 0x02, 0x03 });
    }

    [Fact]
    public async Task IndexFile_RuntimeCheckpointDamageRebuildsWithoutDoubleCounting()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = temporary.GetPath("usage-runtime-damage.db");
        var logPath = temporary.GetPath($"rollout-runtime-{RootId}.jsonl");
        var timestamp = new DateTimeOffset(
            2026,
            7,
            18,
            18,
            0,
            0,
            TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 100, 50, 0, 20, 5));

        var repository = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await repository.InitializeAsync();
        await repository.IndexFileAsync(logPath, false);

        await using (var connection = new SqliteConnection(
                         $"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var damage = connection.CreateCommand();
            damage.CommandText =
                "UPDATE file_state SET previous_input = -1;";
            Assert.Equal(1, await damage.ExecuteNonQueryAsync());
        }

        var repaired = await repository.IndexFileAsync(logPath, false);
        var snapshot = await repository.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddMinutes(1));

        Assert.True(repaired.WasReset);
        Assert.Equal(1, repaired.InsertedEvents);
        Assert.Equal(120, snapshot.Summary.Total.TotalTokens);
    }

    private static async Task AssertSemanticMetadataDamageResetsCacheAsync(
        string column,
        object damagedValue)
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = temporary.GetPath($"usage-damaged-{column}.db");
        var logPath = temporary.GetPath($"rollout-damaged-{RootId}.jsonl");
        var timestamp = new DateTimeOffset(
            2026,
            7,
            18,
            18,
            0,
            0,
            TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            logPath,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(timestamp, 100, 50, 0, 20, 5));

        var original = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await original.InitializeAsync();
        await original.IndexFileAsync(logPath, false);
        await original.MarkRefreshCompleteAsync(timestamp);

        await using (var connection = new SqliteConnection(
                         $"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var damage = connection.CreateCommand();
            damage.CommandText = $"UPDATE file_state SET {column} = $value;";
            damage.Parameters.AddWithValue("$value", damagedValue);
            Assert.Equal(1, await damage.ExecuteNonQueryAsync());
        }

        var reopened = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await reopened.InitializeAsync();

        Assert.Empty(await reopened.LoadIndexedFileMetadataAsync());
        Assert.False(await reopened.HasCompletedInitialIndexAsync());
        Assert.Equal(
            TokenUsage.Zero,
            (await reopened.QueryPeriodAsync(UsagePeriod.All, timestamp))
                .Summary.Total);
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
