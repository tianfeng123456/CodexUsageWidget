using System.Text;
using CodexUsageWidget.Core;
using Microsoft.Data.Sqlite;

namespace CodexUsageWidget.Tests;

public sealed class ReplayBoundaryMigrationTests
{
    private const string RootId = "11111111-1111-1111-1111-111111111111";
    private const string ChildId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task Initialize_UpgradesTrueLegacyFileStateSchemaAndPreservesRows()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = temporary.GetPath("usage.db");
        const string fileKey = "legacy-child.jsonl";
        var legacyPath = temporary.GetPath(fileKey);

        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = databasePath
                         }.ToString()))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE file_state (
                    file_key TEXT PRIMARY KEY,
                    path TEXT NOT NULL,
                    file_length INTEGER NOT NULL,
                    processed_offset INTEGER NOT NULL,
                    last_write_utc_ticks INTEGER NOT NULL,
                    root_task_id TEXT NULL,
                    own_session_id TEXT NULL,
                    is_child INTEGER NOT NULL,
                    previous_input INTEGER NULL,
                    previous_cached_input INTEGER NULL,
                    previous_cache_write_input INTEGER NULL,
                    previous_output INTEGER NULL,
                    previous_reasoning_output INTEGER NULL,
                    replay_boundary_seen INTEGER NOT NULL,
                    checkpoint_hash TEXT NOT NULL,
                    is_archived INTEGER NOT NULL,
                    last_scan_utc TEXT NOT NULL
                );

                INSERT INTO file_state(
                    file_key,
                    path,
                    file_length,
                    processed_offset,
                    last_write_utc_ticks,
                    root_task_id,
                    own_session_id,
                    is_child,
                    previous_input,
                    previous_cached_input,
                    previous_cache_write_input,
                    previous_output,
                    previous_reasoning_output,
                    replay_boundary_seen,
                    checkpoint_hash,
                    is_archived,
                    last_scan_utc)
                VALUES(
                    $key,
                    $path,
                    123,
                    123,
                    456,
                    $root_id,
                    $child_id,
                    1,
                    100,
                    60,
                    1,
                    10,
                    2,
                    1,
                    'LEGACY_HASH',
                    0,
                    '2026-07-18T01:00:00.0000000Z');
                """;
            create.Parameters.AddWithValue("$key", fileKey);
            create.Parameters.AddWithValue("$path", legacyPath);
            create.Parameters.AddWithValue("$root_id", RootId);
            create.Parameters.AddWithValue("$child_id", ChildId);
            await create.ExecuteNonQueryAsync();
        }

        var repository = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await repository.InitializeAsync();

        var metadata = await repository.LoadIndexedFileMetadataAsync();
        var legacy = Assert.Single(metadata).Value;
        Assert.Equal(fileKey, legacy.FileKey);
        Assert.Equal(123, legacy.FileLength);
        Assert.Equal(123, legacy.ProcessedOffset);
        Assert.True(legacy.NeedsReplayMigration);
        Assert.True(legacy.NeedsTokenAccountingMigration);

        await using var verify = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString());
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText =
            """
            SELECT
                first_replay_boundary_offset,
                root_task_id,
                own_session_id,
                previous_input,
                checkpoint_hash
            FROM file_state
            WHERE file_key = $key;
            """;
        command.Parameters.AddWithValue("$key", fileKey);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(RootId, reader.GetString(1));
        Assert.Equal(ChildId, reader.GetString(2));
        Assert.Equal(100, reader.GetInt64(3));
        Assert.Equal("LEGACY_HASH", reader.GetString(4));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Refresh_MigratesUnchangedLegacyChildOnceAndPersistsBoundary()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(
            home,
            "sessions",
            "2026",
            "07",
            "18");
        Directory.CreateDirectory(sessions);
        var path = System.IO.Path.Combine(
            sessions,
            $"rollout-child-{ChildId}.jsonl");
        var timestamp = new DateTimeOffset(
            2026,
            7,
            18,
            1,
            0,
            0,
            TimeSpan.Zero);

        var metadata = TestLog.SessionMeta(
            ChildId,
            RootId,
            RootId,
            timestamp);
        var replayed = TestLog.TokenCount(
            timestamp,
            100,
            60,
            1,
            10,
            2);
        const string decoy =
            """
            {"timestamp":"2026-07-18T01:00:00Z","type":"response_item","payload":{"type":"message","content":"the text inter_agent_communication_metadata is not a boundary"}}
            """;
        var boundary = TestLog.ReplayBoundary(timestamp.AddMinutes(1));
        var accepted = TestLog.TokenCount(
            timestamp.AddMinutes(2),
            150,
            90,
            2,
            20,
            4);
        await TestLog.WriteLinesAsync(
            path,
            metadata,
            decoy,
            replayed,
            boundary,
            accepted);

        var replayedOffset =
            Utf8LineLength(metadata) +
            Utf8LineLength(decoy);
        var boundaryOffset = replayedOffset + Utf8LineLength(replayed);
        var acceptedOffset = boundaryOffset + Utf8LineLength(boundary);
        var file = new FileInfo(path);
        var checkpointHash =
            await SharedFileAccess.ComputeCheckpointHashAsync(path, file.Length);
        var databasePath = temporary.GetPath("usage.db");

        // Initialize the current schema, then seed the state produced by the
        // legacy indexer: the replay prefix and suffix are both present, the
        // old boolean says a boundary was seen, but its first offset is absent.
        var repository = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await repository.InitializeAsync();
        await SeedLegacyRowsAsync(
            databasePath,
            path,
            file,
            checkpointHash,
            replayedOffset,
            acceptedOffset,
            timestamp);

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: databasePath,
                TimeZone: TimeZoneInfo.Utc));
        await service.OpenAsync();
        Assert.True(service.HasCompletedInitialIndex);

        var legacy = await service.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));
        Assert.Equal(170, legacy.Summary.Total.TotalTokens);

        await service.RefreshAsync();
        var migrated = await service.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.Equal(
            new TokenUsage(50, 30, 1, 10, 2),
            migrated.Summary.Total);
        await AssertPersistedMigrationAsync(
            databasePath,
            boundaryOffset,
            expectedEventCount: 1);

        var secondRefresh = await service.RefreshAsync();
        var repeated = await service.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));

        Assert.Equal(migrated.Summary.Total, repeated.Summary.Total);
        Assert.Equal(0, secondRefresh.FilesChanged);
        Assert.Equal(0, secondRefresh.BytesProcessed);
        await AssertPersistedMigrationAsync(
            databasePath,
            boundaryOffset,
            expectedEventCount: 1);
    }

    [Fact]
    public async Task Refresh_MissingBoundaryPreservesLegacyDataAndPendingState()
    {
        using var temporary = new TemporaryDirectory();
        var home = temporary.GetPath("home");
        var sessions = System.IO.Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var path = System.IO.Path.Combine(
            sessions,
            $"rollout-child-{ChildId}.jsonl");
        var timestamp = new DateTimeOffset(
            2026,
            7,
            18,
            1,
            0,
            0,
            TimeSpan.Zero);
        var metadata = TestLog.SessionMeta(
            ChildId,
            RootId,
            RootId,
            timestamp);
        var first = TestLog.TokenCount(timestamp, 100, 60, 1, 10, 2);
        var second = TestLog.TokenCount(
            timestamp.AddMinutes(1),
            150,
            90,
            2,
            20,
            4);
        await TestLog.WriteLinesAsync(path, metadata, first, second);

        var firstOffset = Utf8LineLength(metadata);
        var secondOffset = firstOffset + Utf8LineLength(first);
        var file = new FileInfo(path);
        var checkpointHash =
            await SharedFileAccess.ComputeCheckpointHashAsync(path, file.Length);
        var databasePath = temporary.GetPath("usage.db");
        var repository = new UsageRepository(databasePath, TimeZoneInfo.Utc);
        await repository.InitializeAsync();
        await SeedLegacyRowsAsync(
            databasePath,
            path,
            file,
            checkpointHash,
            firstOffset,
            secondOffset,
            timestamp);

        await using var service = new UsageIndexService(
            new UsageIndexOptions(
                CodexHome: home,
                DatabasePath: databasePath,
                TimeZone: TimeZoneInfo.Utc));
        await service.OpenAsync();
        await service.RefreshAsync();
        await service.RefreshAsync();

        var unchanged = await service.QueryPeriodAsync(
            UsagePeriod.All,
            timestamp.AddHours(1));
        Assert.Equal(170, unchanged.Summary.Total.TotalTokens);
        Assert.True(service.HasCompletedInitialIndex);
        await AssertPendingMigrationAsync(databasePath);
    }

    private static async Task SeedLegacyRowsAsync(
        string databasePath,
        string path,
        FileInfo file,
        string checkpointHash,
        long replayedOffset,
        long acceptedOffset,
        DateTimeOffset timestamp)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO file_state(
                file_key,
                path,
                file_length,
                processed_offset,
                last_write_utc_ticks,
                root_task_id,
                own_session_id,
                is_child,
                previous_input,
                previous_cached_input,
                previous_cache_write_input,
                previous_output,
                previous_reasoning_output,
                replay_boundary_seen,
                first_replay_boundary_offset,
                checkpoint_hash,
                is_archived,
                last_scan_utc)
            VALUES(
                $key,
                $path,
                $length,
                $length,
                $write_ticks,
                $root_id,
                $child_id,
                1,
                150,
                90,
                2,
                20,
                4,
                1,
                NULL,
                $hash,
                0,
                $scan_time);

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
            VALUES
                (
                    $key,
                    $replayed_offset,
                    $replayed_time,
                    '2026-07-18',
                    $root_id,
                    100,
                    60,
                    1,
                    10,
                    2
                ),
                (
                    $key,
                    $accepted_offset,
                    $accepted_time,
                    '2026-07-18',
                    $root_id,
                    50,
                    30,
                    1,
                    10,
                    2
                );

            INSERT INTO daily_task_usage(
                local_date,
                root_task_id,
                input_tokens,
                cached_input_tokens,
                cache_write_input_tokens,
                output_tokens,
                reasoning_output_tokens)
            VALUES('2026-07-18', $root_id, 150, 90, 2, 20, 4);

            INSERT INTO tasks(root_task_id, title, title_updated_at)
            VALUES($root_id, 'legacy child root', $scan_time);

            INSERT INTO metadata(key, value)
            VALUES('initial_index_complete', '1');
            """;
        command.Parameters.AddWithValue(
            "$key",
            UsageRepository.GetSourceKey(path));
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$length", file.Length);
        command.Parameters.AddWithValue(
            "$write_ticks",
            file.LastWriteTimeUtc.Ticks);
        command.Parameters.AddWithValue("$root_id", RootId);
        command.Parameters.AddWithValue("$child_id", ChildId);
        command.Parameters.AddWithValue("$hash", checkpointHash);
        command.Parameters.AddWithValue(
            "$replayed_offset",
            replayedOffset);
        command.Parameters.AddWithValue(
            "$accepted_offset",
            acceptedOffset);
        command.Parameters.AddWithValue(
            "$replayed_time",
            timestamp.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue(
            "$accepted_time",
            timestamp.AddMinutes(2).UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue(
            "$scan_time",
            timestamp.AddMinutes(3).UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertPersistedMigrationAsync(
        string databasePath,
        long expectedBoundaryOffset,
        long expectedEventCount)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                first_replay_boundary_offset,
                (
                    SELECT COUNT(*)
                    FROM token_events
                ),
                (
                    SELECT input_tokens + output_tokens
                    FROM daily_task_usage
                    WHERE local_date = '2026-07-18'
                      AND root_task_id = $root_id
                )
            FROM file_state;
            """;
        command.Parameters.AddWithValue("$root_id", RootId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedBoundaryOffset, reader.GetInt64(0));
        Assert.Equal(expectedEventCount, reader.GetInt64(1));
        Assert.Equal(60, reader.GetInt64(2));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task AssertPendingMigrationAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                first_replay_boundary_offset,
                (
                    SELECT COUNT(*)
                    FROM token_events
                ),
                (
                    SELECT input_tokens + output_tokens
                    FROM daily_task_usage
                    WHERE local_date = '2026-07-18'
                      AND root_task_id = $root_id
                )
            FROM file_state;
            """;
        command.Parameters.AddWithValue("$root_id", RootId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(2, reader.GetInt64(1));
        Assert.Equal(170, reader.GetInt64(2));
        Assert.False(await reader.ReadAsync());
    }

    private static long Utf8LineLength(string line) =>
        Encoding.UTF8.GetByteCount(line) + 1L;
}
