using Microsoft.Data.Sqlite;

namespace CodexUsageWidget.Core;

public sealed class UsageIndexService : IAsyncDisposable
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private const int MaximumCorruptIndexBackups = 2;

    private readonly UsageIndexOptions _options;
    private readonly CodexLogParser _parser;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dictionary<string, IndexedFileMetadata> _indexedFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private UsageRepository? _repository;
    private CodexHomePaths? _paths;
    private string? _databasePath;
    private long _sessionIndexLength = -1;
    private long _sessionIndexLastWriteUtcTicks = -1;
    private volatile bool _disposed;
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

    public bool RequiresHistoryBuild =>
        !_hasCompletedInitialIndex ||
        _indexedFiles.Values.Any(
            static file =>
                file.NeedsReplayMigration ||
                file.NeedsTokenAccountingMigration ||
                file.NeedsForkReplayMigration);

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
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_initialized)
            {
                return;
            }

            _paths = CodexHomeLocator.Detect(_options.CodexHome);
            _databasePath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    _options.DatabasePath ?? GetDefaultDatabasePath()));
            try
            {
                await OpenRepositoryAsync(_databasePath, cancellationToken);
            }
            catch (SqliteException exception) when (IsCorruptDatabase(exception))
            {
                await RecoverCorruptRepositoryAsync(exception, cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<RefreshResult> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _refreshLock.WaitAsync(cancellationToken);
        long processedBytes = 0;
        long totalBytes = 0;
        var terminalProgressPublished = false;
        try
        {
            ThrowIfDisposed();
            _isIndexing = true;
            _progress = 0d;
            PublishProgress(
                0,
                0,
                null,
                IndexProgressStage.Preparing);

            if (_indexedFiles.Values.Any(
                    static file => file.NeedsTokenAccountingMigration))
            {
                // Clear the old accounting index once before the full reparse.
                // Resetting files one by one would repeatedly rebuild the same
                // daily aggregates and create avoidable migration work.
                await _repository!.ResetForTokenAccountingMigrationAsync(
                    cancellationToken);
                _indexedFiles.Clear();
                _hasCompletedInitialIndex = false;
            }

            var enumeration = EnumerateSessionFiles(Paths);
            var files = enumeration.Files;
            SessionIndexSnapshot? sessionIndexToRead = null;
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
                        sessionIndexToRead = new SessionIndexSnapshot(
                            observedLength,
                            observedLastWriteUtcTicks);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                        System.Security.SecurityException)
                {
                    // Titles are optional. Token indexing remains available.
                }
            }

            totalBytes = files.Aggregate(
                sessionIndexToRead?.Length ?? 0L,
                static (total, file) => SaturatingAdd(total, file.Length));
            long changedBytes = 0;
            var changedFiles = 0;
            var hadFileReadFailure = enumeration.HadFailures;
            var pendingReplayFiles = new List<SessionFile>();
            var lastProgressUpdate = System.Diagnostics.Stopwatch.GetTimestamp();

            PublishProgress(
                0,
                totalBytes,
                null,
                IndexProgressStage.Reading);

            if (sessionIndexToRead is { } sessionIndexSnapshot)
            {
                try
                {
                    void ReportSessionIndexProgress(long offset)
                    {
                        var boundedOffset = Math.Clamp(
                            offset,
                            0,
                            sessionIndexSnapshot.Length);
                        if (boundedOffset <= processedBytes ||
                            System.Diagnostics.Stopwatch.GetElapsedTime(
                                lastProgressUpdate) < TimeSpan.FromMilliseconds(100))
                        {
                            return;
                        }

                        PublishProgress(
                            boundedOffset,
                            totalBytes,
                            Paths.SessionIndexPath,
                            IndexProgressStage.Reading);
                        lastProgressUpdate =
                            System.Diagnostics.Stopwatch.GetTimestamp();
                    }

                    var titles = await _parser.ParseSessionIndexAsync(
                        Paths.SessionIndexPath,
                        ReportSessionIndexProgress,
                        cancellationToken);
                    await _repository!.StoreTitlesAsync(
                        titles,
                        cancellationToken);

                    // Cache the signature seen before parsing. If Codex
                    // appends while the parser is at EOF, the next refresh
                    // will still observe a different signature and retry.
                    _sessionIndexLength = sessionIndexSnapshot.Length;
                    _sessionIndexLastWriteUtcTicks =
                        sessionIndexSnapshot.LastWriteUtcTicks;
                }
                catch (IOException)
                {
                    // A concurrently replaced index will be retried on the next refresh.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep token data available even if the title index is inaccessible.
                }
                catch (System.Security.SecurityException)
                {
                    // Keep token data available even if policy blocks title metadata.
                }
                finally
                {
                    processedBytes = SaturatingAdd(
                        processedBytes,
                        sessionIndexSnapshot.Length);
                    PublishProgress(
                        processedBytes,
                        totalBytes,
                        Paths.SessionIndexPath,
                        IndexProgressStage.Reading);
                    lastProgressUpdate =
                        System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceKey = UsageRepository.GetSourceKey(file.Path);
                var completedFileBytes = processedBytes;
                if (!IsUnchangedFullyIndexedFile(sourceKey, file))
                {
                    try
                    {
                        void ReportFileProgress(long fileOffset)
                        {
                            var boundedOffset = Math.Clamp(
                                fileOffset,
                                0,
                                file.Length);
                            var candidateBytes = SaturatingAdd(
                                completedFileBytes,
                                boundedOffset);
                            if (candidateBytes <= processedBytes ||
                                System.Diagnostics.Stopwatch.GetElapsedTime(
                                    lastProgressUpdate) < TimeSpan.FromMilliseconds(100))
                            {
                                return;
                            }

                            PublishProgress(
                                candidateBytes,
                                totalBytes,
                                file.Path,
                                IndexProgressStage.Reading);
                            lastProgressUpdate =
                                System.Diagnostics.Stopwatch.GetTimestamp();
                        }

                        var result = await _repository!.IndexFileAsync(
                            file.Path,
                            file.IsArchived,
                            ReportFileProgress,
                            cancellationToken);

                        _indexedFiles[sourceKey] = new IndexedFileMetadata(
                            sourceKey,
                            file.Path,
                            result.IndexedFileLength,
                            result.CurrentOffset,
                            result.IndexedLastWriteUtcTicks,
                            file.IsArchived,
                            result.NeedsReplayMigration,
                            false,
                            false);
                        if (result.NeedsReplayMigration)
                        {
                            pendingReplayFiles.Add(file);
                        }

                        changedBytes = SaturatingAdd(
                            changedBytes,
                            result.BytesProcessed);
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
                    catch (System.Security.SecurityException)
                    {
                        hadFileReadFailure = true;
                    }
                }

                processedBytes = SaturatingAdd(processedBytes, file.Length);
                if (processedBytes >= totalBytes ||
                    System.Diagnostics.Stopwatch.GetElapsedTime(
                        lastProgressUpdate) >= TimeSpan.FromMilliseconds(100))
                {
                    PublishProgress(
                        processedBytes,
                        totalBytes,
                        file.Path,
                        IndexProgressStage.Reading);
                    lastProgressUpdate =
                        System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }

            // Explicit forks can sort before their source task. Resolve those
            // dependencies within this same refresh after every ordinary file
            // has had a chance to register its session id. A bounded number of
            // cheap passes also handles a chain of fork dependencies without
            // creating a background retry loop.
            var unresolvedReplayFiles = pendingReplayFiles;
            for (var attempt = 0;
                 attempt < pendingReplayFiles.Count &&
                 unresolvedReplayFiles.Count > 0;
                 attempt++)
            {
                var nextUnresolved = new List<SessionFile>();
                var resolvedAny = false;
                foreach (var file in unresolvedReplayFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceKey = UsageRepository.GetSourceKey(file.Path);
                    try
                    {
                        var result = await _repository!.IndexFileAsync(
                            file.Path,
                            file.IsArchived,
                            cancellationToken: cancellationToken);
                        _indexedFiles[sourceKey] = new IndexedFileMetadata(
                            sourceKey,
                            file.Path,
                            result.IndexedFileLength,
                            result.CurrentOffset,
                            result.IndexedLastWriteUtcTicks,
                            file.IsArchived,
                            result.NeedsReplayMigration,
                            false,
                            false);
                        if (result.NeedsReplayMigration)
                        {
                            nextUnresolved.Add(file);
                            continue;
                        }

                        resolvedAny = true;
                        changedBytes = SaturatingAdd(
                            changedBytes,
                            result.BytesProcessed);
                        if (result.BytesProcessed > 0 || result.WasReset)
                        {
                            changedFiles++;
                        }
                    }
                    catch (Exception exception) when (
                        exception is FileNotFoundException or
                            IOException or
                            UnauthorizedAccessException or
                            System.Security.SecurityException)
                    {
                        hadFileReadFailure = true;
                    }
                }

                unresolvedReplayFiles = nextUnresolved;
                if (!resolvedAny)
                {
                    break;
                }
            }

            hadFileReadFailure |= unresolvedReplayFiles.Count > 0;

            var completedAt = DateTimeOffset.UtcNow;
            if (!hadFileReadFailure)
            {
                PublishProgress(
                    processedBytes,
                    totalBytes,
                    null,
                    IndexProgressStage.Finalizing);
                await _repository!.MarkRefreshCompleteAsync(
                    completedAt,
                    cancellationToken);
                _hasCompletedInitialIndex = true;
                PublishProgress(
                    totalBytes,
                    totalBytes,
                    null,
                    IndexProgressStage.Completed);
            }
            else
            {
                PublishProgress(
                    processedBytes,
                    totalBytes,
                    null,
                    IndexProgressStage.Incomplete);
            }

            terminalProgressPublished = true;

            return new RefreshResult(
                files.Count,
                changedFiles,
                changedBytes,
                completedAt,
                !hadFileReadFailure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            throw;
        }
        catch
        {
            if (!terminalProgressPublished)
            {
                PublishProgress(
                    processedBytes,
                    totalBytes,
                    null,
                    IndexProgressStage.Incomplete);
            }

            throw;
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumTailBytesPerFile);

        var files = EnumerateSessionFiles(Paths).Files
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
            catch (System.Security.SecurityException)
            {
                // One policy-denied session must not hide the remaining quota.
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

    public async Task<RefreshResult> RebuildIndexAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _progress = 0d;
            PublishProgress(
                0,
                0,
                null,
                IndexProgressStage.Preparing);
            try
            {
                await _repository!.ResetAsync(cancellationToken);
                ResetCachedIndexState();
            }
            catch (SqliteException exception) when (
                IsCorruptDatabase(exception))
            {
                // The index is a derived cache. If corruption appears after
                // startup, the explicit rebuild action must still be able to
                // recover instead of depending on a successful DELETE from
                // the already-damaged database.
                await RecoverCorruptRepositoryAsync(
                    exception,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            throw;
        }
        catch
        {
            PublishProgress(
                0,
                0,
                null,
                IndexProgressStage.Incomplete);
            throw;
        }
        finally
        {
            _refreshLock.Release();
        }

        return await RefreshAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _refreshLock.WaitAsync();
        try
        {
            _disposed = true;
        }
        finally
        {
            // Keep the semaphore alive so a caller that passed the initial
            // disposed check just before this method can wake, observe the
            // disposed state, and release its acquired slot safely.
            _refreshLock.Release();
        }
    }

    private static SessionFileEnumeration EnumerateSessionFiles(
        CodexHomePaths paths)
    {
        var files = new Dictionary<string, SessionFile>(
            StringComparer.OrdinalIgnoreCase);

        var hadFailures =
            !AddFiles(files, paths.ArchivedSessionsDirectory, true);
        hadFailures |= !AddFiles(files, paths.SessionsDirectory, false);
        var ordered = files.Values
            .OrderBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SessionFileEnumeration(ordered, hadFailures);
    }

    private static bool AddFiles(
        Dictionary<string, SessionFile> files,
        string directory,
        bool isArchived)
    {
        var complete = true;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var currentDirectory))
        {
            string[] filePaths;
            try
            {
                filePaths = Directory.GetFiles(
                    currentDirectory,
                    "*.jsonl",
                    SearchOption.TopDirectoryOnly);
            }
            catch (DirectoryNotFoundException)
            {
                // A not-yet-created source folder and a concurrently removed
                // child folder both contribute no files and are complete.
                continue;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    System.Security.SecurityException)
            {
                complete = false;
                continue;
            }

            foreach (var path in filePaths)
            {
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

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
                    complete = false;
                }
                catch (UnauthorizedAccessException)
                {
                    complete = false;
                }
                catch (System.Security.SecurityException)
                {
                    complete = false;
                }
            }

            string[] childDirectories;
            try
            {
                childDirectories = Directory.GetDirectories(
                    currentDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    System.Security.SecurityException)
            {
                complete = false;
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                try
                {
                    if ((File.GetAttributes(childDirectory) &
                         FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(childDirectory);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                        System.Security.SecurityException)
                {
                    complete = false;
                }
            }
        }

        return complete;
    }

    private void PublishProgress(
        long processedBytes,
        long totalBytes,
        string? currentFile,
        IndexProgressStage stage)
    {
        var args = new IndexProgressChangedEventArgs(
            processedBytes,
            totalBytes,
            currentFile,
            stage);
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

    private async Task OpenRepositoryAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var candidate = new UsageRepository(
            databasePath,
            _options.TimeZone,
            _parser);
        await candidate.InitializeAsync(cancellationToken);
        await candidate.EnsureTimeZoneCompatibilityAsync(cancellationToken);
        var indexedFiles = await candidate.LoadIndexedFileMetadataAsync(
            cancellationToken);
        var hasCompletedInitialIndex =
            await candidate.HasCompletedInitialIndexAsync(cancellationToken);

        _repository = candidate;
        _indexedFiles.Clear();
        foreach (var (key, metadata) in indexedFiles)
        {
            _indexedFiles[key] = metadata;
        }

        _hasCompletedInitialIndex = hasCompletedInitialIndex;
    }

    private async Task RecoverCorruptRepositoryAsync(
        SqliteException corruption,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var databasePath = _databasePath ?? throw new InvalidOperationException(
            "索引数据库路径尚未初始化。");

        _repository = null;
        ResetCachedIndexState();
        SqliteConnection.ClearAllPools();
        var quarantinedPath = QuarantineDatabaseFiles(databasePath, corruption);
        LocalDiagnosticLog.TryWrite(
            Path.GetDirectoryName(databasePath) ?? Path.GetTempPath(),
            $"index-database-quarantined {Path.GetFileName(quarantinedPath)}",
            corruption);
        await OpenRepositoryAsync(databasePath, cancellationToken);
        CleanupOldCorruptBackups(databasePath);
    }

    private void ResetCachedIndexState()
    {
        _hasCompletedInitialIndex = false;
        _indexedFiles.Clear();
        _sessionIndexLength = -1;
        _sessionIndexLastWriteUtcTicks = -1;
    }

    private static bool IsCorruptDatabase(SqliteException exception) =>
        exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase;

    private static string QuarantineDatabaseFiles(
        string databasePath,
        Exception corruption)
    {
        var suffix =
            $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var quarantinedPath = databasePath + suffix;
        try
        {
            MoveIfPresent(databasePath, quarantinedPath);
            MoveIfPresent(databasePath + "-wal", quarantinedPath + "-wal");
            MoveIfPresent(databasePath + "-shm", quarantinedPath + "-shm");
            return quarantinedPath;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                "损坏的本地用量索引无法隔离，未创建替代索引。",
                new AggregateException(corruption, exception));
        }
    }

    private static void MoveIfPresent(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
    }

    private static void CleanupOldCorruptBackups(string databasePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrWhiteSpace(directory) ||
                !Directory.Exists(directory))
            {
                return;
            }

            var prefix = Path.GetFileName(databasePath) + ".corrupt-";
            var backups = Directory
                .EnumerateFiles(
                    directory,
                    prefix + "*",
                    SearchOption.TopDirectoryOnly)
                .Where(static path =>
                    !path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
                .Select(static path => new FileInfo(path))
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .ThenByDescending(
                    static file => file.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Skip(MaximumCorruptIndexBackups)
                .ToArray();
            foreach (var backup in backups)
            {
                File.Delete(backup.FullName);
                File.Delete(backup.FullName + "-wal");
                File.Delete(backup.FullName + "-shm");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            // Recovery already succeeded. Retention cleanup is best effort and
            // must never make a usable fresh index fail to open.
        }
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
               !indexed.NeedsReplayMigration &&
               !indexed.NeedsTokenAccountingMigration &&
               !indexed.NeedsForkReplayMigration &&
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

    private sealed record SessionIndexSnapshot(
        long Length,
        long LastWriteUtcTicks);

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        if (right < 0 && left < long.MinValue - right)
        {
            return long.MinValue;
        }

        return left + right;
    }

    private sealed record SessionFileEnumeration(
        IReadOnlyList<SessionFile> Files,
        bool HadFailures);
}
