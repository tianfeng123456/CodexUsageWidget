namespace CodexUsageWidget.Core;

public sealed class UsageIndexService : IAsyncDisposable
{
    private readonly UsageIndexOptions _options;
    private readonly CodexLogParser _parser;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dictionary<string, IndexedFileMetadata> _indexedFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private UsageRepository? _repository;
    private CodexHomePaths? _paths;
    private long _sessionIndexLength = -1;
    private long _sessionIndexLastWriteUtcTicks = -1;
    private bool _disposed;
    private bool _hasCompletedInitialIndex;
    private bool _initialized;
    private bool _isIndexing;
    private double _progress;

    public UsageIndexService(
        UsageIndexOptions? options = null,
        CodexLogParser? parser = null)
    {
        _options = options ?? new UsageIndexOptions();
        _parser = parser ?? new CodexLogParser();
    }

    public event EventHandler<IndexProgressChangedEventArgs>? ProgressChanged;

    public CodexHomePaths Paths =>
        _paths ?? throw new InvalidOperationException("服务尚未初始化。");

    public bool IsIndexing => _isIndexing;

    public double IndexProgress => _progress;

    public bool HasCompletedInitialIndex => _hasCompletedInitialIndex;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            return;
        }

        await OpenAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// Opens (or creates) the local SQLite index without scanning any rollout
    /// logs. This lets callers display an existing dashboard immediately and
    /// schedule <see cref="RefreshAsync"/> in the background.
    /// </summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            return;
        }

        _paths = CodexHomeLocator.Detect(_options.CodexHome);
        var databasePath = _options.DatabasePath ?? GetDefaultDatabasePath();
        _repository = new UsageRepository(
            databasePath,
            _options.TimeZone,
            _parser);
        await _repository.InitializeAsync(cancellationToken);
        var indexedFiles = await _repository.LoadIndexedFileMetadataAsync(
            cancellationToken);
        _indexedFiles.Clear();
        foreach (var (key, metadata) in indexedFiles)
        {
            _indexedFiles[key] = metadata;
        }

        _hasCompletedInitialIndex =
            await _repository.HasCompletedInitialIndexAsync(cancellationToken);
        _initialized = true;
    }

    public async Task<RefreshResult> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            _isIndexing = true;
            _progress = 0d;

            var files = EnumerateSessionFiles(Paths);
            var totalBytes = files.Sum(static file => file.Length);
            long processedBytes = 0;
            long changedBytes = 0;
            var changedFiles = 0;
            var hadFileReadFailure = false;
            var lastProgressUpdate = System.Diagnostics.Stopwatch.GetTimestamp();
            PublishProgress(0, totalBytes, null, false);

            if (File.Exists(Paths.SessionIndexPath))
            {
                try
                {
                    var sessionIndex = new FileInfo(Paths.SessionIndexPath);
                    var observedLength = sessionIndex.Length;
                    var observedLastWriteUtcTicks =
                        sessionIndex.LastWriteTimeUtc.Ticks;
                    if (observedLength != _sessionIndexLength ||
                        observedLastWriteUtcTicks !=
                        _sessionIndexLastWriteUtcTicks)
                    {
                        var titles = await _parser.ParseSessionIndexAsync(
                            Paths.SessionIndexPath,
                            cancellationToken);
                        await _repository!.StoreTitlesAsync(
                            titles,
                            cancellationToken);

                        // Cache the signature seen before parsing. If Codex
                        // appends while the parser is at EOF, the next refresh
                        // will still observe a different signature and retry.
                        _sessionIndexLength = observedLength;
                        _sessionIndexLastWriteUtcTicks =
                            observedLastWriteUtcTicks;
                    }
                }
                catch (IOException)
                {
                    // A concurrently replaced index will be retried on the next refresh.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep token data available even if the title index is inaccessible.
                }
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceKey = UsageRepository.GetSourceKey(file.Path);
                if (!IsUnchangedFullyIndexedFile(sourceKey, file))
                {
                    try
                    {
                        var result = await _repository!.IndexFileAsync(
                            file.Path,
                            file.IsArchived,
                            cancellationToken);

                        _indexedFiles[sourceKey] = new IndexedFileMetadata(
                            sourceKey,
                            file.Path,
                            file.Length,
                            result.CurrentOffset,
                            file.LastWriteUtcTicks,
                            file.IsArchived);
                        changedBytes += result.BytesProcessed;
                        if (result.BytesProcessed > 0 || result.WasReset)
                        {
                            changedFiles++;
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        // The active session may have moved into the archive after enumeration.
                        hadFileReadFailure = true;
                    }
                    catch (IOException)
                    {
                        // A transient writer or move race is retried on the next refresh.
                        hadFileReadFailure = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // One unreadable file must not make the whole dashboard unavailable.
                        hadFileReadFailure = true;
                    }
                }

                processedBytes += file.Length;
                if (processedBytes >= totalBytes ||
                    System.Diagnostics.Stopwatch.GetElapsedTime(
                        lastProgressUpdate) >= TimeSpan.FromMilliseconds(100))
                {
                    PublishProgress(
                        processedBytes,
                        totalBytes,
                        file.Path,
                        false);
                    lastProgressUpdate =
                        System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }

            var completedAt = DateTimeOffset.UtcNow;
            if (!hadFileReadFailure)
            {
                await _repository!.MarkRefreshCompleteAsync(
                    completedAt,
                    cancellationToken);
                _hasCompletedInitialIndex = true;
            }

            PublishProgress(totalBytes, totalBytes, null, true);

            return new RefreshResult(
                files.Count,
                changedFiles,
                changedBytes,
                completedAt);
        }
        finally
        {
            _isIndexing = false;
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Quickly samples the tails of the most recently written rollout logs.
    /// It does not mutate the index and is intended for the startup quota badge.
    /// </summary>
    public async Task<RateLimitSnapshot?> ReadLatestRateLimitsFromRecentLogsAsync(
        int maximumFiles = 16,
        int maximumTailBytesPerFile = 512 * 1024,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (maximumFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        }

        if (maximumTailBytesPerFile <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTailBytesPerFile));
        }

        var files = EnumerateSessionFiles(Paths)
            .OrderByDescending(static file => file.LastWriteUtcTicks)
            .ThenByDescending(static file => file.Length)
            .Take(maximumFiles)
            .ToArray();

        RateLimitSnapshot? latest = null;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var candidate = await _parser.ParseLatestRateLimitFromTailAsync(
                    file.Path,
                    maximumTailBytesPerFile,
                    cancellationToken);
                if (candidate is not null &&
                    (latest is null || IsPreferredRateLimit(candidate, latest)))
                {
                    latest = candidate;
                }
            }
            catch (FileNotFoundException)
            {
                // An active session may have moved to the archive.
            }
            catch (IOException)
            {
                // A concurrently replaced file will be retried by normal refresh.
            }
            catch (UnauthorizedAccessException)
            {
                // One unreadable session must not hide the remaining quota.
            }
        }

        return latest;
    }

    public Task<PeriodSnapshot> QueryPeriodAsync(
        UsagePeriod period,
        CancellationToken cancellationToken = default) =>
        QueryPeriodAsync(period, DateTimeOffset.Now, cancellationToken);

    public async Task<PeriodSnapshot> QueryPeriodAsync(
        UsagePeriod period,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await _repository!.QueryPeriodAsync(
            period,
            now,
            _options.TopTaskCount,
            cancellationToken);
    }

    /// <summary>
    /// Reads the already-indexed exact Codex weekly allowance observations and
    /// returns each local calendar date's final direct observation and its
    /// difference from the immediately preceding calendar date when that date
    /// also has an observation.
    /// This is a read-only operation and does not refresh or mutate the index.
    /// </summary>
    public async Task<IReadOnlyList<DailyWeeklyRateLimitUsage>>
        QueryWeeklyRateLimitDailyUsageAsync(
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive,
            CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await _repository!.QueryWeeklyRateLimitDailyUsageAsync(
            fromInclusive,
            toExclusive,
            cancellationToken);
    }

    public async Task<DashboardSnapshot> GetDashboardAsync(
        UsagePeriod period,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await QueryPeriodAsync(period, cancellationToken);
        return new DashboardSnapshot(
            snapshot,
            snapshot.RateLimits,
            snapshot.LastUpdated,
            _isIndexing,
            _progress);
    }

    public async Task RebuildIndexAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            await _repository!.ResetAsync(cancellationToken);
            _hasCompletedInitialIndex = false;
            _indexedFiles.Clear();
            _sessionIndexLength = -1;
            _sessionIndexLastWriteUtcTicks = -1;
        }
        finally
        {
            _refreshLock.Release();
        }

        await RefreshAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _refreshLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private static IReadOnlyList<SessionFile> EnumerateSessionFiles(
        CodexHomePaths paths)
    {
        var files = new Dictionary<string, SessionFile>(
            StringComparer.OrdinalIgnoreCase);

        AddFiles(files, paths.ArchivedSessionsDirectory, true);
        AddFiles(files, paths.SessionsDirectory, false);
        return files.Values
            .OrderBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddFiles(
        Dictionary<string, SessionFile> files,
        string directory,
        bool isArchived)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(
                directory,
                "*.jsonl",
                SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                var key = UsageRepository.GetSourceKey(path);
                var candidate = new SessionFile(
                    info.FullName,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    isArchived);

                if (!files.TryGetValue(key, out var existing) ||
                    candidate.Length > existing.Length ||
                    (candidate.Length == existing.Length &&
                     !candidate.IsArchived &&
                     existing.IsArchived))
                {
                    files[key] = candidate;
                }
            }
            catch (IOException)
            {
                // The file may have moved to the archive while enumerating.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip unreadable files without stopping the entire index.
            }
        }
    }

    private void PublishProgress(
        long processedBytes,
        long totalBytes,
        string? currentFile,
        bool isComplete)
    {
        var args = new IndexProgressChangedEventArgs(
            processedBytes,
            totalBytes,
            currentFile,
            isComplete);
        _progress = args.Progress;
        ProgressChanged?.Invoke(this, args);
    }

    private static string GetDefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return Path.Combine(
            localAppData,
            "CodexUsageWidget",
            "usage-index.db");
    }

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (!_initialized || _repository is null || _paths is null)
        {
            throw new InvalidOperationException(
                "UsageIndexService 尚未初始化，请先调用 InitializeAsync。");
        }
    }

    private bool IsUnchangedFullyIndexedFile(
        string sourceKey,
        SessionFile file)
    {
        return _indexedFiles.TryGetValue(sourceKey, out var indexed) &&
               indexed.ProcessedOffset == file.Length &&
               indexed.FileLength == file.Length &&
               indexed.LastWriteUtcTicks == file.LastWriteUtcTicks &&
               indexed.IsArchived == file.IsArchived &&
               string.Equals(
                   indexed.Path,
                   file.Path,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static bool IsPreferredRateLimit(
        RateLimitSnapshot candidate,
        RateLimitSnapshot current)
    {
        var candidateIsCodex = string.Equals(
            candidate.LimitId,
            "codex",
            StringComparison.OrdinalIgnoreCase);
        var currentIsCodex = string.Equals(
            current.LimitId,
            "codex",
            StringComparison.OrdinalIgnoreCase);
        return candidateIsCodex != currentIsCodex
            ? candidateIsCodex
            : candidate.Timestamp > current.Timestamp;
    }

    private sealed record SessionFile(
        string Path,
        long Length,
        long LastWriteUtcTicks,
        bool IsArchived);
}
