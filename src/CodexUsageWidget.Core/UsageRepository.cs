using Microsoft.Data.Sqlite;

namespace CodexUsageWidget.Core;

public sealed record FileIndexResult(
    string SourceKey,
    long PreviousOffset,
    long CurrentOffset,
    int InsertedEvents,
    int MalformedLines,
    bool WasReset)
{
    public long BytesProcessed => Math.Max(0, CurrentOffset - PreviousOffset);
}

public sealed record IndexedFileMetadata(
    string FileKey,
    string Path,
    long FileLength,
    long ProcessedOffset,
    long LastWriteUtcTicks,
    bool IsArchived);

public sealed class UsageRepository
{
    private const int WeeklyRateLimitWindowMinutes = 10_080;
    private static readonly TimeSpan WeeklyResetTimestampTolerance =
        TimeSpan.FromSeconds(60);

    private readonly string _connectionString;
    private readonly TimeZoneInfo _timeZone;
    private readonly CodexLogParser _parser;
    private bool _initialized;

    public UsageRepository(
        string databasePath,
        TimeZoneInfo? timeZone = null,
        CodexLogParser? parser = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(databasePath));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _parser = parser ?? new CodexLogParser();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS file_state (
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

            CREATE TABLE IF NOT EXISTS token_events (
                file_key TEXT NOT NULL,
                event_offset INTEGER NOT NULL,
                timestamp_utc TEXT NOT NULL,
                local_date TEXT NOT NULL,
                root_task_id TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                cache_write_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_output_tokens INTEGER NOT NULL,
                PRIMARY KEY (file_key, event_offset),
                FOREIGN KEY (file_key) REFERENCES file_state(file_key) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_token_events_timestamp
                ON token_events(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_token_events_task_date
                ON token_events(root_task_id, local_date);

            CREATE TABLE IF NOT EXISTS daily_task_usage (
                local_date TEXT NOT NULL,
                root_task_id TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL,
                cache_write_input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_output_tokens INTEGER NOT NULL,
                PRIMARY KEY (local_date, root_task_id)
            );

            CREATE TABLE IF NOT EXISTS rate_limit_events (
                file_key TEXT NOT NULL,
                event_offset INTEGER NOT NULL,
                timestamp_utc TEXT NOT NULL,
                limit_id TEXT NULL,
                limit_name TEXT NULL,
                plan_type TEXT NULL,
                primary_used_percent REAL NULL,
                primary_window_minutes INTEGER NULL,
                primary_resets_at TEXT NULL,
                secondary_used_percent REAL NULL,
                secondary_window_minutes INTEGER NULL,
                secondary_resets_at TEXT NULL,
                PRIMARY KEY (file_key, event_offset),
                FOREIGN KEY (file_key) REFERENCES file_state(file_key) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_rate_limit_latest
                ON rate_limit_events(limit_id, timestamp_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_rate_limit_codex_latest
                ON rate_limit_events(timestamp_utc DESC, event_offset DESC)
                WHERE lower(COALESCE(limit_id, '')) = 'codex';
            CREATE INDEX IF NOT EXISTS ix_rate_limit_latest_any
                ON rate_limit_events(timestamp_utc DESC, event_offset DESC);

            CREATE TABLE IF NOT EXISTS tasks (
                root_task_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                title_updated_at TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        _initialized = true;
    }

    public async Task<IReadOnlyDictionary<string, IndexedFileMetadata>>
        LoadIndexedFileMetadataAsync(
            CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var result = new Dictionary<string, IndexedFileMetadata>(
            StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                file_key,
                path,
                file_length,
                processed_offset,
                last_write_utc_ticks,
                is_archived
            FROM file_state;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var metadata = new IndexedFileMetadata(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5) != 0);
            result[metadata.FileKey] = metadata;
        }

        return result;
    }

    public async Task<FileIndexResult> IndexFileAsync(
        string path,
        bool isArchived,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Codex 日志文件不存在。", path);
        }

        var sourceKey = GetSourceKey(path);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var state = await LoadFileStateAsync(connection, sourceKey, cancellationToken);
        var wasReset = false;

        if (state is not null)
        {
            var continuityBroken = file.Length < state.ProcessedOffset;
            if (!continuityBroken &&
                state.ProcessedOffset > 0 &&
                !string.IsNullOrEmpty(state.CheckpointHash))
            {
                var continuityHash = await SharedFileAccess.ComputeCheckpointHashAsync(
                    path,
                    state.ProcessedOffset,
                    cancellationToken);
                continuityBroken = !string.Equals(
                    continuityHash,
                    state.CheckpointHash,
                    StringComparison.Ordinal);
            }

            if (continuityBroken)
            {
                await ResetFileAsync(connection, sourceKey, cancellationToken);
                state = null;
                wasReset = true;
            }
        }

        var previousOffset = state?.ProcessedOffset ?? 0;
        var checkpoint = state?.ToCheckpoint() ?? LogParseCheckpoint.Empty;
        var parseResult = await _parser.ParseFileAsync(
            path,
            sourceKey,
            checkpoint,
            cancellationToken);

        var currentHash = await SharedFileAccess.ComputeCheckpointHashAsync(
            path,
            parseResult.Checkpoint.Offset,
            cancellationToken);

        using var transaction = connection.BeginTransaction();
        await UpsertFileStateAsync(
            connection,
            transaction,
            sourceKey,
            path,
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            parseResult.Checkpoint,
            currentHash,
            isArchived,
            cancellationToken);

        var insertedEvents = 0;
        foreach (var delta in parseResult.Deltas)
        {
            var localDate = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(delta.Timestamp, _timeZone).DateTime);

            var inserted = await InsertTokenEventAsync(
                connection,
                transaction,
                delta,
                localDate,
                cancellationToken);
            if (!inserted)
            {
                continue;
            }

            insertedEvents++;
            await AddToDailyUsageAsync(
                connection,
                transaction,
                localDate,
                delta.RootTaskId,
                delta.Usage,
                cancellationToken);
        }

        foreach (var rateLimit in parseResult.RateLimits)
        {
            await InsertRateLimitAsync(
                connection,
                transaction,
                sourceKey,
                rateLimit,
                cancellationToken);
        }

        var rootTaskId = parseResult.Checkpoint.RootTaskId;
        if (!string.IsNullOrWhiteSpace(rootTaskId))
        {
            await EnsureTaskAsync(
                connection,
                transaction,
                rootTaskId,
                cancellationToken);
        }

        transaction.Commit();

        return new FileIndexResult(
            sourceKey,
            previousOffset,
            parseResult.Checkpoint.Offset,
            insertedEvents,
            parseResult.MalformedLineCount,
            wasReset);
    }

    public async Task StoreTitlesAsync(
        IEnumerable<SessionTitleEntry> titles,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(titles);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title.RootTaskId) ||
                string.IsNullOrWhiteSpace(title.Title))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO tasks(root_task_id, title, title_updated_at)
                VALUES($id, $title, $updated)
                ON CONFLICT(root_task_id) DO UPDATE SET
                    title = excluded.title,
                    title_updated_at = excluded.title_updated_at
                WHERE tasks.title_updated_at IS NULL
                   OR (
                       excluded.title_updated_at IS NOT NULL
                       AND excluded.title_updated_at >= tasks.title_updated_at
                   );
                """;
            command.Parameters.AddWithValue("$id", title.RootTaskId);
            command.Parameters.AddWithValue("$title", title.Title);
            command.Parameters.AddWithValue(
                "$updated",
                (object?)title.UpdatedAt?.UtcDateTime.ToString("O") ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<PeriodSnapshot> QueryPeriodAsync(
        UsagePeriod period,
        DateTimeOffset? now = null,
        int topTaskCount = 9,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (topTaskCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(topTaskCount),
                "前 N 项数量必须大于 0。");
        }

        var referenceNow = now ?? DateTimeOffset.Now;
        var localNow = TimeZoneInfo.ConvertTime(referenceNow, _timeZone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var (fromDate, toDate) = GetDateRange(period, today);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var predicates = new List<string>();
        var usageSource = "daily_task_usage";
        var usageCte = string.Empty;

        if (period == UsagePeriod.Last7Days)
        {
            var windowEndUtc = referenceNow.ToUniversalTime();
            var windowStartUtc = windowEndUtc.AddDays(-7);
            fromDate = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(windowStartUtc, _timeZone).DateTime);
            toDate = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(windowEndUtc, _timeZone).DateTime);

            command.Parameters.AddWithValue(
                "$from",
                fromDate.Value.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue(
                "$to",
                toDate.Value.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue(
                "$start_utc",
                windowStartUtc.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue(
                "$end_utc",
                windowEndUtc.UtcDateTime.ToString("O"));

            usageSource = "period_usage";
            usageCte =
                """
                period_usage AS (
                    -- Complete local dates wholly inside the rolling window can use
                    -- the compact daily aggregate.
                    SELECT
                        d.root_task_id,
                        d.input_tokens,
                        d.cached_input_tokens,
                        d.cache_write_input_tokens,
                        d.output_tokens,
                        d.reasoning_output_tokens
                    FROM daily_task_usage d
                    WHERE d.local_date > $from
                      AND d.local_date < $to

                    UNION ALL

                    -- The two partial boundary dates need event timestamps so that
                    -- "近7日" means the preceding 168 hours, not seven calendar days.
                    SELECT
                        e.root_task_id,
                        e.input_tokens,
                        e.cached_input_tokens,
                        e.cache_write_input_tokens,
                        e.output_tokens,
                        e.reasoning_output_tokens
                    FROM token_events e
                    WHERE e.timestamp_utc >= $start_utc
                      AND e.timestamp_utc <= $end_utc
                      AND (e.local_date = $from OR e.local_date = $to)
                ),
                """;
        }
        else if (fromDate is not null)
        {
            predicates.Add("u.local_date >= $from");
            command.Parameters.AddWithValue("$from", fromDate.Value.ToString("yyyy-MM-dd"));
        }

        if (period != UsagePeriod.Last7Days && toDate is not null)
        {
            predicates.Add("u.local_date <= $to");
            command.Parameters.AddWithValue("$to", toDate.Value.ToString("yyyy-MM-dd"));
        }

        command.Parameters.AddWithValue("$top_count", topTaskCount);
        command.CommandText =
            $"""
            WITH
            {usageCte}
            task_usage AS (
                SELECT
                    u.root_task_id,
                    COALESCE(t.title, '') AS title,
                    SUM(u.input_tokens) AS input_tokens,
                    SUM(u.cached_input_tokens) AS cached_input_tokens,
                    SUM(u.cache_write_input_tokens) AS cache_write_input_tokens,
                    SUM(u.output_tokens) AS output_tokens,
                    SUM(u.reasoning_output_tokens) AS reasoning_output_tokens,
                    SUM(u.input_tokens) + SUM(u.output_tokens) AS total_tokens,
                    CASE WHEN EXISTS(
                        SELECT 1
                        FROM file_state f
                        WHERE f.root_task_id = u.root_task_id
                          AND f.own_session_id = f.root_task_id
                          AND f.is_archived = 0
                    ) THEN 0 ELSE 1 END AS is_archived
                FROM {usageSource} u
                LEFT JOIN tasks t ON t.root_task_id = u.root_task_id
                {(predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates))}
                GROUP BY u.root_task_id, t.title
            ),
            summary AS (
                SELECT
                    COUNT(*) AS task_count,
                    COALESCE(SUM(input_tokens), 0) AS input_tokens,
                    COALESCE(SUM(cached_input_tokens), 0) AS cached_input_tokens,
                    COALESCE(SUM(cache_write_input_tokens), 0) AS cache_write_input_tokens,
                    COALESCE(SUM(output_tokens), 0) AS output_tokens,
                    COALESCE(SUM(reasoning_output_tokens), 0) AS reasoning_output_tokens
                FROM task_usage
            ),
            top_usage AS (
                SELECT *
                FROM task_usage
                ORDER BY total_tokens DESC, root_task_id COLLATE NOCASE
                LIMIT $top_count
            )
            SELECT
                s.task_count,
                s.input_tokens,
                s.cached_input_tokens,
                s.cache_write_input_tokens,
                s.output_tokens,
                s.reasoning_output_tokens,
                top.root_task_id,
                top.title,
                top.input_tokens,
                top.cached_input_tokens,
                top.cache_write_input_tokens,
                top.output_tokens,
                top.reasoning_output_tokens,
                top.is_archived
            FROM summary s
            LEFT JOIN top_usage top ON 1 = 1
            ORDER BY top.total_tokens DESC, top.root_task_id COLLATE NOCASE;
            """;

        var topRaw = new List<RawTaskUsage>(topTaskCount);
        var total = TokenUsage.Zero;
        var taskCount = 0;
        var summaryRead = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!summaryRead)
                {
                    var rawTaskCount = reader.GetInt64(0);
                    taskCount = (int)Math.Min(rawTaskCount, int.MaxValue);
                    total = new TokenUsage(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5));
                    summaryRead = true;
                }

                if (reader.IsDBNull(6))
                {
                    continue;
                }

                var rootTaskId = reader.GetString(6);
                var title = reader.GetString(7);
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = $"未命名任务 {ShortId(rootTaskId)}";
                }

                topRaw.Add(new RawTaskUsage(
                    rootTaskId,
                    title,
                    new TokenUsage(
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        reader.GetInt64(11),
                        reader.GetInt64(12)),
                    reader.GetInt64(13) != 0));
            }
        }

        var topTotal = topRaw.Aggregate(
            TokenUsage.Zero,
            static (sum, task) => sum + task.Usage);
        var other = Subtract(total, topTotal);
        var topPercent = total.TotalTokens == 0
            ? 0d
            : (double)topTotal.TotalTokens / total.TotalTokens * 100d;

        var topTasks = topRaw
            .Select(task => new TaskUsageSnapshot(
                task.RootTaskId,
                task.Title,
                task.Usage,
                total.TotalTokens == 0
                    ? 0d
                    : (double)task.Usage.TotalTokens / total.TotalTokens * 100d,
                task.IsArchived))
            .ToArray();

        var latestRateLimit = await GetLatestRateLimitAsync(
            connection,
            cancellationToken);
        var lastUpdated = await GetLastRefreshAsync(connection, cancellationToken) ??
                          DateTimeOffset.MinValue;

        return new PeriodSnapshot(
            period,
            fromDate,
            toDate,
            topTasks,
            new UsageSummary(
                total,
                taskCount,
                topTotal,
                other,
                topPercent),
            latestRateLimit,
            lastUpdated);
    }

    /// <summary>
    /// Reconstructs the observed daily increase of the exact <c>codex</c>
    /// 10,080-minute allowance meter for every local calendar date touched by a
    /// half-open interval. Reset timestamps within a small clock/rounding
    /// tolerance are clustered and the best-supported schedule becomes the
    /// single canonical timeline. Its monotonic high-water mark prevents
    /// concurrent stale snapshots, overlapping schedules, and reset drops from
    /// being counted as new usage. Existing observations are read only; this
    /// method never mutates the index.
    /// </summary>
    public async Task<IReadOnlyList<DailyWeeklyRateLimitUsage>>
        QueryWeeklyRateLimitDailyUsageAsync(
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive,
            CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var fromUtc = fromInclusive.ToUniversalTime();
        var toUtc = toExclusive.ToUniversalTime();
        if (toUtc <= fromUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toExclusive),
                "结束时间必须晚于开始时间。");
        }

        var firstDate = ToLocalDate(fromUtc);
        var lastDate = ToLocalDate(toUtc.AddTicks(-1));
        var historyFromUtc = fromUtc.AddMinutes(
            -WeeklyRateLimitWindowMinutes);
        var days = CreateDailyRateLimitBuilders(
            firstDate,
            lastDate,
            fromUtc,
            toUtc);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH weekly_observations AS (
                SELECT
                    timestamp_utc,
                    CASE
                        WHEN primary_window_minutes = $weekly_minutes
                            THEN primary_used_percent
                        ELSE secondary_used_percent
                    END AS used_percent,
                    CASE
                        WHEN primary_window_minutes = $weekly_minutes
                            THEN primary_resets_at
                        ELSE secondary_resets_at
                    END AS resets_at
                FROM rate_limit_events
                WHERE lower(COALESCE(limit_id, '')) = 'codex'
                  AND timestamp_utc >= $history_from_utc
                  AND timestamp_utc < $to_utc
                  AND (
                      (primary_window_minutes = $weekly_minutes
                       AND primary_used_percent IS NOT NULL)
                      OR
                      (secondary_window_minutes = $weekly_minutes
                       AND secondary_used_percent IS NOT NULL)
                  )
            )
            SELECT DISTINCT
                timestamp_utc,
                used_percent,
                resets_at
            FROM weekly_observations
            WHERE used_percent IS NOT NULL
            ORDER BY
                timestamp_utc,
                used_percent,
                resets_at;
            """;
        command.Parameters.AddWithValue(
            "$weekly_minutes",
            WeeklyRateLimitWindowMinutes);
        command.Parameters.AddWithValue(
            "$history_from_utc",
            historyFromUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue(
            "$to_utc",
            toUtc.UtcDateTime.ToString("O"));

        var resetClusters = await LoadWeeklyResetClustersAsync(
            connection,
            historyFromUtc,
            toUtc,
            cancellationToken);
        WeeklyResetCluster? activeResetCluster = null;
        WeeklyRateLimitEpochState? activeEpoch = null;
        var activeSelectionIsAmbiguous = false;
        WeeklyRateLimitEpochState? unknownResetEpoch = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadWeeklyRateLimitObservation(reader, out var observation))
            {
                continue;
            }

            if (observation.Timestamp >= toUtc)
            {
                continue;
            }

            // A snapshot that still names an already expired reset window is a
            // late write from another session. It must not alter the new
            // window's high-water mark or the day that received the stale row.
            if (observation.ResetsAt is { } resetAt &&
                resetAt <= observation.Timestamp)
            {
                continue;
            }

            WeeklyRateLimitEpochState epoch;
            if (observation.ResetsAt is { } observedReset)
            {
                if (!resetClusters.TryGetCluster(
                        observedReset,
                        out var observationCluster))
                {
                    continue;
                }

                if (activeResetCluster is null ||
                    activeResetCluster.MaximumResetAt <=
                    observation.Timestamp)
                {
                    var selection = resetClusters.SelectCanonicalCluster(
                        observation.Timestamp,
                        activeResetCluster);
                    if (selection.Cluster is null)
                    {
                        continue;
                    }

                    if (!ReferenceEquals(
                            activeResetCluster,
                            selection.Cluster))
                    {
                        activeResetCluster = selection.Cluster;
                        activeEpoch = null;
                        activeSelectionIsAmbiguous =
                            selection.IsAmbiguous;
                    }
                }

                // Only one reset schedule may be active at a time. All reset
                // timestamps inside the selected jitter cluster feed one
                // shared high-water mark; a different overlapping cluster is
                // a competing session snapshot, not another allowance bucket
                // to add to the day.
                if (!ReferenceEquals(
                        observationCluster,
                        activeResetCluster))
                {
                    continue;
                }

                if (activeEpoch is null)
                {
                    var resetWindowStartedAt =
                        activeResetCluster.CanonicalResetAt.AddMinutes(
                            -WeeklyRateLimitWindowMinutes);
                    var startsInsideRequestedInterval =
                        resetWindowStartedAt >= fromUtc &&
                        resetWindowStartedAt < toUtc;
                    activeEpoch = new WeeklyRateLimitEpochState(
                        startsInsideRequestedInterval
                            ? 0d
                            : observation.UsedPercent,
                        baselineKnown:
                            observation.Timestamp < fromUtc ||
                            startsInsideRequestedInterval,
                        activeSelectionIsAmbiguous);
                }

                epoch = activeEpoch;
            }
            else
            {
                // Older log formats can omit resets_at. Keep a single
                // best-effort timeline only when no dated reset schedule is
                // available, and report it as partial.
                if (resetClusters.Count > 0)
                {
                    continue;
                }

                if (unknownResetEpoch is null)
                {
                    unknownResetEpoch = new WeeklyRateLimitEpochState(
                        observation.UsedPercent,
                        baselineKnown: observation.Timestamp < fromUtc,
                        isAmbiguous: true);
                }

                epoch = unknownResetEpoch;
            }

            if (epoch.LastAcceptedTimestamp == observation.Timestamp &&
                epoch.LastAcceptedUsedPercent == observation.UsedPercent)
            {
                continue;
            }

            if (observation.UsedPercent < epoch.HighWaterUsedPercent)
            {
                // Concurrent sessions can emit an older cumulative snapshot
                // after a newer one. It is not an accepted observation and
                // must not replace the day's time or sample count.
                continue;
            }

            var increase =
                observation.UsedPercent - epoch.HighWaterUsedPercent;
            if (observation.UsedPercent > epoch.HighWaterUsedPercent)
            {
                epoch.HighWaterUsedPercent = observation.UsedPercent;
            }

            epoch.LastAcceptedTimestamp = observation.Timestamp;
            epoch.LastAcceptedUsedPercent = observation.UsedPercent;

            if (observation.Timestamp < fromUtc)
            {
                epoch.BaselineKnown = true;
                continue;
            }

            var localDate = ToLocalDate(observation.Timestamp);
            if (!days.TryGetValue(localDate, out var day))
            {
                continue;
            }

            day.ConsumedPercentagePoints ??= 0d;
            day.ConsumedPercentagePoints += increase;
            day.LastObservedUsedPercent = epoch.HighWaterUsedPercent;
            day.LastObservedAt = TimeZoneInfo.ConvertTime(
                observation.Timestamp,
                _timeZone);
            if (!epoch.BaselineKnown || epoch.IsAmbiguous)
            {
                day.IsPartial = true;
                epoch.BaselineKnown = true;
            }

            if (day.ObservationCount < int.MaxValue)
            {
                day.ObservationCount++;
            }
        }

        DailyWeeklyRateLimitUsageBuilder? previousDay = null;
        foreach (var day in days.Values)
        {
            if (day.ObservationCount > 0 &&
                !day.IsPartial &&
                previousDay is
                {
                    ObservationCount: > 0,
                    IsPartial: false,
                })
            {
                day.ChangeFromPreviousDayPercentagePoints =
                    day.ConsumedPercentagePoints!.Value -
                    previousDay.ConsumedPercentagePoints!.Value;
            }

            previousDay = day;
        }

        return days.Values
            .Select(static day => day.ToSnapshot())
            .ToArray();
    }

    private static async Task<WeeklyResetClusterIndex>
        LoadWeeklyResetClustersAsync(
            SqliteConnection connection,
            DateTimeOffset historyFromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH weekly_observations AS (
                SELECT
                    timestamp_utc,
                    CASE
                        WHEN primary_window_minutes = $weekly_minutes
                            THEN primary_used_percent
                        ELSE secondary_used_percent
                    END AS used_percent,
                    CASE
                        WHEN primary_window_minutes = $weekly_minutes
                            THEN primary_resets_at
                        ELSE secondary_resets_at
                    END AS resets_at
                FROM rate_limit_events
                WHERE lower(COALESCE(limit_id, '')) = 'codex'
                  AND timestamp_utc >= $history_from_utc
                  AND timestamp_utc < $to_utc
                  AND (
                      (primary_window_minutes = $weekly_minutes
                       AND primary_used_percent IS NOT NULL)
                      OR
                      (secondary_window_minutes = $weekly_minutes
                       AND secondary_used_percent IS NOT NULL)
                  )
            )
            SELECT
                resets_at,
                COUNT(*) AS support_count,
                MIN(timestamp_utc) AS first_observed_at,
                MAX(timestamp_utc) AS last_observed_at
            FROM (
                SELECT DISTINCT
                    timestamp_utc,
                    used_percent,
                    resets_at
                FROM weekly_observations
                WHERE used_percent IS NOT NULL
                  AND resets_at IS NOT NULL
            )
            GROUP BY resets_at
            ORDER BY resets_at;
            """;
        command.Parameters.AddWithValue(
            "$weekly_minutes",
            WeeklyRateLimitWindowMinutes);
        command.Parameters.AddWithValue(
            "$history_from_utc",
            historyFromUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue(
            "$to_utc",
            toUtc.UtcDateTime.ToString("O"));

        var candidates = new List<WeeklyResetCandidate>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!TryParseUtcTimestamp(reader.GetString(0), out var resetAt) ||
                !TryParseUtcTimestamp(reader.GetString(2), out var firstAt) ||
                !TryParseUtcTimestamp(reader.GetString(3), out var lastAt))
            {
                continue;
            }

            candidates.Add(new WeeklyResetCandidate(
                resetAt,
                reader.GetInt64(1),
                firstAt,
                lastAt));
        }

        return WeeklyResetClusterIndex.Create(
            candidates,
            WeeklyResetTimestampTolerance,
            TimeSpan.FromMinutes(WeeklyRateLimitWindowMinutes));
    }

    private static bool TryParseUtcTimestamp(
        string value,
        out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out timestamp);

    public async Task MarkRefreshCompleteAsync(
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO metadata(key, value)
            VALUES('last_refresh_utc', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO metadata(key, value)
            VALUES('initial_index_complete', '1')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$value", completedAt.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task<bool> HasCompletedInitialIndexAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CASE WHEN EXISTS(
                SELECT 1
                FROM metadata
                WHERE (key = 'initial_index_complete' AND value = '1')
                   OR key = 'last_refresh_utc'
            ) THEN 1 ELSE 0 END;
            """;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[]
                 {
                     "token_events",
                     "daily_task_usage",
                     "rate_limit_events",
                     "file_state",
                     "tasks",
                     "metadata"
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<long> GetIndexedOffsetAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var state = await LoadFileStateAsync(
            connection,
            GetSourceKey(path),
            cancellationToken);
        return state?.ProcessedOffset ?? 0;
    }

    public static string GetSourceKey(string path) =>
        Path.GetFileName(path).ToLowerInvariant();

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task<FileState?> LoadFileStateAsync(
        SqliteConnection connection,
        string sourceKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
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
                is_archived
            FROM file_state
            WHERE file_key = $key;
            """;
        command.Parameters.AddWithValue("$key", sourceKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        TokenUsage? previous = null;
        if (!reader.IsDBNull(7))
        {
            previous = new TokenUsage(
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11));
        }

        return new FileState(
            sourceKey,
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt64(6) != 0,
            previous,
            reader.GetInt64(12) != 0,
            reader.GetString(13),
            reader.GetInt64(14) != 0);
    }

    private static async Task UpsertFileStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceKey,
        string path,
        long fileLength,
        long lastWriteUtcTicks,
        LogParseCheckpoint checkpoint,
        string checkpointHash,
        bool isArchived,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
                checkpoint_hash,
                is_archived,
                last_scan_utc)
            VALUES(
                $key,
                $path,
                $length,
                $offset,
                $write_ticks,
                $root_id,
                $own_id,
                $is_child,
                $previous_input,
                $previous_cached,
                $previous_cache_write,
                $previous_output,
                $previous_reasoning,
                $boundary,
                $hash,
                $archived,
                $scan_time)
            ON CONFLICT(file_key) DO UPDATE SET
                path = excluded.path,
                file_length = excluded.file_length,
                processed_offset = excluded.processed_offset,
                last_write_utc_ticks = excluded.last_write_utc_ticks,
                root_task_id = excluded.root_task_id,
                own_session_id = excluded.own_session_id,
                is_child = excluded.is_child,
                previous_input = excluded.previous_input,
                previous_cached_input = excluded.previous_cached_input,
                previous_cache_write_input = excluded.previous_cache_write_input,
                previous_output = excluded.previous_output,
                previous_reasoning_output = excluded.previous_reasoning_output,
                replay_boundary_seen = excluded.replay_boundary_seen,
                checkpoint_hash = excluded.checkpoint_hash,
                is_archived = excluded.is_archived,
                last_scan_utc = excluded.last_scan_utc;
            """;

        command.Parameters.AddWithValue("$key", sourceKey);
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$length", fileLength);
        command.Parameters.AddWithValue("$offset", checkpoint.Offset);
        command.Parameters.AddWithValue("$write_ticks", lastWriteUtcTicks);
        command.Parameters.AddWithValue(
            "$root_id",
            (object?)checkpoint.RootTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$own_id",
            (object?)checkpoint.OwnSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_child", checkpoint.IsChildSession ? 1 : 0);
        AddNullableUsageParameters(command, checkpoint.PreviousCumulative);
        command.Parameters.AddWithValue(
            "$boundary",
            checkpoint.ReplayBoundarySeen ? 1 : 0);
        command.Parameters.AddWithValue("$hash", checkpointHash);
        command.Parameters.AddWithValue("$archived", isArchived ? 1 : 0);
        command.Parameters.AddWithValue(
            "$scan_time",
            DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddNullableUsageParameters(
        SqliteCommand command,
        TokenUsage? usage)
    {
        command.Parameters.AddWithValue(
            "$previous_input",
            usage is null ? DBNull.Value : usage.Value.InputTokens);
        command.Parameters.AddWithValue(
            "$previous_cached",
            usage is null ? DBNull.Value : usage.Value.CachedInputTokens);
        command.Parameters.AddWithValue(
            "$previous_cache_write",
            usage is null ? DBNull.Value : usage.Value.CacheWriteInputTokens);
        command.Parameters.AddWithValue(
            "$previous_output",
            usage is null ? DBNull.Value : usage.Value.OutputTokens);
        command.Parameters.AddWithValue(
            "$previous_reasoning",
            usage is null ? DBNull.Value : usage.Value.ReasoningOutputTokens);
    }

    private static async Task<bool> InsertTokenEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TokenUsageDelta delta,
        DateOnly localDate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO token_events(
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
                $root_id,
                $input,
                $cached,
                $cache_write,
                $output,
                $reasoning);
            """;
        command.Parameters.AddWithValue("$key", delta.SourceKey);
        command.Parameters.AddWithValue("$offset", delta.EventOffset);
        command.Parameters.AddWithValue(
            "$timestamp",
            delta.Timestamp.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$root_id", delta.RootTaskId);
        AddUsageParameters(command, delta.Usage);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task AddToDailyUsageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly localDate,
        string rootTaskId,
        TokenUsage usage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO daily_task_usage(
                local_date,
                root_task_id,
                input_tokens,
                cached_input_tokens,
                cache_write_input_tokens,
                output_tokens,
                reasoning_output_tokens)
            VALUES(
                $date,
                $root_id,
                $input,
                $cached,
                $cache_write,
                $output,
                $reasoning)
            ON CONFLICT(local_date, root_task_id) DO UPDATE SET
                input_tokens = input_tokens + excluded.input_tokens,
                cached_input_tokens = cached_input_tokens + excluded.cached_input_tokens,
                cache_write_input_tokens = cache_write_input_tokens + excluded.cache_write_input_tokens,
                output_tokens = output_tokens + excluded.output_tokens,
                reasoning_output_tokens = reasoning_output_tokens + excluded.reasoning_output_tokens;
            """;
        command.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$root_id", rootTaskId);
        AddUsageParameters(command, usage);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRateLimitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceKey,
        RateLimitSnapshotAtOffset rateLimit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO rate_limit_events(
                file_key,
                event_offset,
                timestamp_utc,
                limit_id,
                limit_name,
                plan_type,
                primary_used_percent,
                primary_window_minutes,
                primary_resets_at,
                secondary_used_percent,
                secondary_window_minutes,
                secondary_resets_at)
            VALUES(
                $key,
                $offset,
                $timestamp,
                $limit_id,
                $limit_name,
                $plan_type,
                $primary_used,
                $primary_window,
                $primary_reset,
                $secondary_used,
                $secondary_window,
                $secondary_reset);
            """;

        var snapshot = rateLimit.Snapshot;
        command.Parameters.AddWithValue("$key", sourceKey);
        command.Parameters.AddWithValue("$offset", rateLimit.EventOffset);
        command.Parameters.AddWithValue(
            "$timestamp",
            snapshot.Timestamp.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue(
            "$limit_id",
            (object?)snapshot.LimitId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$limit_name",
            (object?)snapshot.LimitName ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$plan_type",
            (object?)snapshot.PlanType ?? DBNull.Value);
        AddWindowParameters(command, "primary", snapshot.Primary);
        AddWindowParameters(command, "secondary", snapshot.Secondary);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddWindowParameters(
        SqliteCommand command,
        string prefix,
        RateLimitWindowSnapshot? window)
    {
        command.Parameters.AddWithValue(
            $"${prefix}_used",
            window is null ? DBNull.Value : window.UsedPercent);
        command.Parameters.AddWithValue(
            $"${prefix}_window",
            window?.WindowMinutes is null
                ? DBNull.Value
                : window.WindowMinutes.Value);
        command.Parameters.AddWithValue(
            $"${prefix}_reset",
            (object?)window?.ResetsAt?.UtcDateTime.ToString("O") ?? DBNull.Value);
    }

    private static async Task EnsureTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rootTaskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO tasks(root_task_id, title, title_updated_at)
            VALUES($id, '', NULL);
            """;
        command.Parameters.AddWithValue("$id", rootTaskId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ResetFileAsync(
        SqliteConnection connection,
        string sourceKey,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[] { "token_events", "rate_limit_events" })
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {table} WHERE file_key = $key;";
            delete.Parameters.AddWithValue("$key", sourceKey);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteState = connection.CreateCommand())
        {
            deleteState.Transaction = transaction;
            deleteState.CommandText = "DELETE FROM file_state WHERE file_key = $key;";
            deleteState.Parameters.AddWithValue("$key", sourceKey);
            await deleteState.ExecuteNonQueryAsync(cancellationToken);
        }

        await RebuildDailyUsageAsync(connection, transaction, cancellationToken);
        transaction.Commit();
    }

    private static async Task RebuildDailyUsageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM daily_task_usage;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var rebuild = connection.CreateCommand();
        rebuild.Transaction = transaction;
        rebuild.CommandText =
            """
            INSERT INTO daily_task_usage(
                local_date,
                root_task_id,
                input_tokens,
                cached_input_tokens,
                cache_write_input_tokens,
                output_tokens,
                reasoning_output_tokens)
            SELECT
                local_date,
                root_task_id,
                SUM(input_tokens),
                SUM(cached_input_tokens),
                SUM(cache_write_input_tokens),
                SUM(output_tokens),
                SUM(reasoning_output_tokens)
            FROM token_events
            GROUP BY local_date, root_task_id;
            """;
        await rebuild.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<RateLimitSnapshot?> GetLatestRateLimitAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var codex = await ReadLatestRateLimitAsync(
            connection,
            codexOnly: true,
            cancellationToken);
        return codex ?? await ReadLatestRateLimitAsync(
            connection,
            codexOnly: false,
            cancellationToken);
    }

    private static async Task<RateLimitSnapshot?> ReadLatestRateLimitAsync(
        SqliteConnection connection,
        bool codexOnly,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            codexOnly
                ? """
                  SELECT
                      timestamp_utc,
                      limit_id,
                      limit_name,
                      plan_type,
                      primary_used_percent,
                      primary_window_minutes,
                      primary_resets_at,
                      secondary_used_percent,
                      secondary_window_minutes,
                      secondary_resets_at
                  FROM rate_limit_events
                  WHERE lower(COALESCE(limit_id, '')) = 'codex'
                  ORDER BY timestamp_utc DESC, event_offset DESC
                  LIMIT 1;
                  """
                : """
            SELECT
                timestamp_utc,
                limit_id,
                limit_name,
                plan_type,
                primary_used_percent,
                primary_window_minutes,
                primary_resets_at,
                secondary_used_percent,
                secondary_window_minutes,
                secondary_resets_at
            FROM rate_limit_events
            ORDER BY timestamp_utc DESC, event_offset DESC
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var timestamp = DateTimeOffset.Parse(
            reader.GetString(0),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        var primary = ReadWindow(reader, 4, 5, 6);
        var secondary = ReadWindow(reader, 7, 8, 9);

        return new RateLimitSnapshot(
            timestamp,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            primary,
            secondary);
    }

    private static RateLimitWindowSnapshot? ReadWindow(
        SqliteDataReader reader,
        int usedOrdinal,
        int windowOrdinal,
        int resetOrdinal)
    {
        if (reader.IsDBNull(usedOrdinal))
        {
            return null;
        }

        DateTimeOffset? reset = null;
        if (!reader.IsDBNull(resetOrdinal) &&
            DateTimeOffset.TryParse(
                reader.GetString(resetOrdinal),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            reset = parsed;
        }

        return new RateLimitWindowSnapshot(
            reader.GetDouble(usedOrdinal),
            reader.IsDBNull(windowOrdinal) ? null : reader.GetInt32(windowOrdinal),
            reset);
    }

    private SortedDictionary<DateOnly, DailyWeeklyRateLimitUsageBuilder>
        CreateDailyRateLimitBuilders(
            DateOnly firstDate,
            DateOnly lastDate,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc)
    {
        var result =
            new SortedDictionary<DateOnly, DailyWeeklyRateLimitUsageBuilder>();
        var date = firstDate;
        while (true)
        {
            var dayStartUtc = GetLocalDayStartUtc(date);
            var nextDate = date.AddDays(1);
            var dayEndUtc = GetLocalDayStartUtc(nextDate);
            var intervalCoversOnlyPartOfDate =
                fromUtc > dayStartUtc || toUtc < dayEndUtc;
            result.Add(
                date,
                new DailyWeeklyRateLimitUsageBuilder(
                    date,
                    intervalCoversOnlyPartOfDate));

            if (date == lastDate)
            {
                break;
            }

            date = nextDate;
        }

        return result;
    }

    private DateOnly ToLocalDate(DateTimeOffset timestamp) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(timestamp, _timeZone).DateTime);

    private DateTimeOffset GetLocalDayStartUtc(DateOnly date)
    {
        var localMidnight = DateTime.SpecifyKind(
            date.ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(localMidnight, _timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static bool TryReadWeeklyRateLimitObservation(
        SqliteDataReader reader,
        out WeeklyRateLimitObservation observation)
    {
        observation = null!;
        if (reader.IsDBNull(0) ||
            reader.IsDBNull(1) ||
            !DateTimeOffset.TryParse(
                reader.GetString(0),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return false;
        }

        DateTimeOffset? resetsAt = null;
        if (reader.FieldCount > 2 &&
            !reader.IsDBNull(2) &&
            DateTimeOffset.TryParse(
                reader.GetString(2),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsedReset))
        {
            resetsAt = parsedReset;
        }

        observation = new WeeklyRateLimitObservation(
            timestamp,
            Math.Clamp(reader.GetDouble(1), 0d, 100d),
            resetsAt);
        return true;
    }

    private static async Task<DateTimeOffset?> GetLastRefreshAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT value FROM metadata WHERE key = 'last_refresh_utc' LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static void AddUsageParameters(
        SqliteCommand command,
        TokenUsage usage)
    {
        command.Parameters.AddWithValue("$input", usage.InputTokens);
        command.Parameters.AddWithValue("$cached", usage.CachedInputTokens);
        command.Parameters.AddWithValue("$cache_write", usage.CacheWriteInputTokens);
        command.Parameters.AddWithValue("$output", usage.OutputTokens);
        command.Parameters.AddWithValue("$reasoning", usage.ReasoningOutputTokens);
    }

    private static TokenUsage Subtract(TokenUsage total, TokenUsage part) => new(
        Math.Max(0, total.InputTokens - part.InputTokens),
        Math.Max(0, total.CachedInputTokens - part.CachedInputTokens),
        Math.Max(0, total.CacheWriteInputTokens - part.CacheWriteInputTokens),
        Math.Max(0, total.OutputTokens - part.OutputTokens),
        Math.Max(0, total.ReasoningOutputTokens - part.ReasoningOutputTokens));

    private static (DateOnly? From, DateOnly? To) GetDateRange(
        UsagePeriod period,
        DateOnly today) =>
        period switch
        {
            UsagePeriod.Today => (today, today),
            UsagePeriod.Last7Days => (today.AddDays(-7), today),
            UsagePeriod.Month => (
                new DateOnly(today.Year, today.Month, 1),
                today),
            UsagePeriod.All => (null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(period))
        };

    private static string ShortId(string id) =>
        id.Length <= 8 ? id : id[..8];

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "UsageRepository 尚未初始化，请先调用 InitializeAsync。");
        }
    }

    private sealed record RawTaskUsage(
        string RootTaskId,
        string Title,
        TokenUsage Usage,
        bool IsArchived);

    private sealed record WeeklyRateLimitObservation(
        DateTimeOffset Timestamp,
        double UsedPercent,
        DateTimeOffset? ResetsAt);

    private sealed record WeeklyResetCandidate(
        DateTimeOffset ResetAt,
        long SupportCount,
        DateTimeOffset FirstObservedAt,
        DateTimeOffset LastObservedAt);

    private sealed record WeeklyResetClusterSelection(
        WeeklyResetCluster? Cluster,
        bool IsAmbiguous);

    private sealed class WeeklyResetClusterIndex
    {
        private readonly IReadOnlyList<WeeklyResetCluster> _clusters;
        private readonly IReadOnlyDictionary<long, WeeklyResetCluster>
            _clustersByResetTicks;
        private readonly TimeSpan _tolerance;
        private readonly TimeSpan _window;

        private WeeklyResetClusterIndex(
            IReadOnlyList<WeeklyResetCluster> clusters,
            IReadOnlyDictionary<long, WeeklyResetCluster>
                clustersByResetTicks,
            TimeSpan tolerance,
            TimeSpan window)
        {
            _clusters = clusters;
            _clustersByResetTicks = clustersByResetTicks;
            _tolerance = tolerance;
            _window = window;
        }

        public int Count => _clusters.Count;

        public static WeeklyResetClusterIndex Create(
            IEnumerable<WeeklyResetCandidate> candidates,
            TimeSpan tolerance,
            TimeSpan window)
        {
            var clusters = new List<WeeklyResetCluster>();
            var byResetTicks = new Dictionary<long, WeeklyResetCluster>();
            foreach (var candidate in candidates.OrderBy(
                         static candidate => candidate.ResetAt))
            {
                var cluster = clusters.Count > 0 &&
                              candidate.ResetAt -
                              clusters[^1].MinimumResetAt <= tolerance
                    ? clusters[^1]
                    : null;
                if (cluster is null)
                {
                    cluster = new WeeklyResetCluster(candidate);
                    clusters.Add(cluster);
                }
                else
                {
                    cluster.Add(candidate);
                }

                byResetTicks[candidate.ResetAt.UtcTicks] = cluster;
            }

            return new WeeklyResetClusterIndex(
                clusters,
                byResetTicks,
                tolerance,
                window);
        }

        public bool TryGetCluster(
            DateTimeOffset resetAt,
            out WeeklyResetCluster cluster) =>
            _clustersByResetTicks.TryGetValue(
                resetAt.UtcTicks,
                out cluster!);

        public WeeklyResetClusterSelection SelectCanonicalCluster(
            DateTimeOffset observationTimestamp,
            WeeklyResetCluster? previous)
        {
            var eligible = _clusters
                .Where(cluster =>
                    cluster.MaximumResetAt > observationTimestamp &&
                    cluster.MinimumResetAt - _window - _tolerance <=
                    observationTimestamp &&
                    cluster.LastObservedAt >= observationTimestamp &&
                    (previous is null ||
                     cluster.MinimumResetAt >
                     previous.MaximumResetAt + _tolerance))
                .OrderByDescending(static cluster =>
                    cluster.SupportCount)
                .ThenByDescending(static cluster =>
                    cluster.ObservationSpan)
                .ThenByDescending(static cluster =>
                    cluster.LastObservedAt)
                .ThenBy(static cluster =>
                    cluster.CanonicalResetAt)
                .ToArray();

            return new WeeklyResetClusterSelection(
                eligible.FirstOrDefault(),
                eligible.Length > 1 ||
                eligible.FirstOrDefault()?.IsCanonicalCandidateAmbiguous ==
                true);
        }
    }

    private sealed class WeeklyResetCluster
    {
        private WeeklyResetCandidate _canonicalCandidate;
        private long _runnerUpSupportCount;

        public WeeklyResetCluster(WeeklyResetCandidate candidate)
        {
            _canonicalCandidate = candidate;
            MinimumResetAt = candidate.ResetAt;
            MaximumResetAt = candidate.ResetAt;
            FirstObservedAt = candidate.FirstObservedAt;
            LastObservedAt = candidate.LastObservedAt;
            SupportCount = candidate.SupportCount;
        }

        public DateTimeOffset MinimumResetAt { get; private set; }

        public DateTimeOffset MaximumResetAt { get; private set; }

        public DateTimeOffset CanonicalResetAt =>
            _canonicalCandidate.ResetAt;

        public DateTimeOffset FirstObservedAt { get; private set; }

        public DateTimeOffset LastObservedAt { get; private set; }

        public long SupportCount { get; private set; }

        public bool IsCanonicalCandidateAmbiguous =>
            _runnerUpSupportCount == _canonicalCandidate.SupportCount;

        public TimeSpan ObservationSpan =>
            LastObservedAt - FirstObservedAt;

        public void Add(WeeklyResetCandidate candidate)
        {
            MinimumResetAt = candidate.ResetAt < MinimumResetAt
                ? candidate.ResetAt
                : MinimumResetAt;
            MaximumResetAt = candidate.ResetAt > MaximumResetAt
                ? candidate.ResetAt
                : MaximumResetAt;
            FirstObservedAt =
                candidate.FirstObservedAt < FirstObservedAt
                    ? candidate.FirstObservedAt
                    : FirstObservedAt;
            LastObservedAt =
                candidate.LastObservedAt > LastObservedAt
                    ? candidate.LastObservedAt
                    : LastObservedAt;
            SupportCount += candidate.SupportCount;

            if (candidate.SupportCount >
                _canonicalCandidate.SupportCount)
            {
                _runnerUpSupportCount = Math.Max(
                    _runnerUpSupportCount,
                    _canonicalCandidate.SupportCount);
                _canonicalCandidate = candidate;
            }
            else
            {
                _runnerUpSupportCount = Math.Max(
                    _runnerUpSupportCount,
                    candidate.SupportCount);
                if (candidate.SupportCount ==
                    _canonicalCandidate.SupportCount &&
                    candidate.LastObservedAt >
                    _canonicalCandidate.LastObservedAt)
                {
                    _canonicalCandidate = candidate;
                }
            }
        }
    }

    private sealed class WeeklyRateLimitEpochState(
        double highWaterUsedPercent,
        bool baselineKnown,
        bool isAmbiguous)
    {
        public double HighWaterUsedPercent { get; set; } =
            highWaterUsedPercent;

        public bool BaselineKnown { get; set; } = baselineKnown;

        public bool IsAmbiguous { get; } = isAmbiguous;

        public DateTimeOffset? LastAcceptedTimestamp { get; set; }

        public double? LastAcceptedUsedPercent { get; set; }
    }

    private sealed class DailyWeeklyRateLimitUsageBuilder(
        DateOnly localDate,
        bool isPartial)
    {
        public DateOnly LocalDate { get; } = localDate;

        public double? ConsumedPercentagePoints { get; set; }

        public double? ChangeFromPreviousDayPercentagePoints { get; set; }

        public double? LastObservedUsedPercent { get; set; }

        public DateTimeOffset? LastObservedAt { get; set; }

        public int ObservationCount { get; set; }

        public bool IsPartial { get; set; } = isPartial;

        public DailyWeeklyRateLimitUsage ToSnapshot() => new(
            LocalDate,
            ConsumedPercentagePoints,
            ChangeFromPreviousDayPercentagePoints,
            LastObservedUsedPercent,
            LastObservedAt,
            ObservationCount,
            IsPartial || ObservationCount == 0);
    }

    private sealed record FileState(
        string FileKey,
        string Path,
        long FileLength,
        long ProcessedOffset,
        long LastWriteUtcTicks,
        string? RootTaskId,
        string? OwnSessionId,
        bool IsChild,
        TokenUsage? Previous,
        bool ReplayBoundarySeen,
        string CheckpointHash,
        bool IsArchived)
    {
        public LogParseCheckpoint ToCheckpoint() => new(
            ProcessedOffset,
            RootTaskId,
            OwnSessionId,
            IsChild,
            Previous,
            ReplayBoundarySeen);
    }
}
