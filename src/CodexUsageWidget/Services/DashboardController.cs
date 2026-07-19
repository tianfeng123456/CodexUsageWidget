using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;
using CodexUsageWidget.Core;
using CodexUsageWidget.ViewModels;

namespace CodexUsageWidget.Services;

public sealed class DashboardController : IAsyncDisposable
{
    private const long QuietQuotaDebounceMilliseconds = 1500;
    private const long MaximumQuotaDebounceMilliseconds = 3000;
    private const int QuotaCalibrationFileCount = 16;

    private readonly MainViewModel viewModel;
    private readonly Dispatcher dispatcher;
    private readonly string appDataDirectory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sourceGate = new(1, 1);
    private readonly SemaphoreSlim indexGate = new(1, 1);
    private readonly SemaphoreSlim quotaGate = new(1, 1);
    private readonly System.Threading.Timer quotaDebounceTimer;
    private readonly ConcurrentQueue<QuotaFileChange> quotaChanges = new();
    private readonly object panelRefreshLock = new();
    private readonly object quotaProcessorLock = new();
    private readonly object rateLimitLock = new();
    private readonly object recoveryTaskLock = new();
    private readonly object watcherLock = new();
    private readonly List<WatcherRegistration> watchers = [];
    private readonly List<SourceContext> retiredSources = [];
    private readonly HashSet<Task> quotaProcessorTasks = [];
    private readonly HashSet<Task> recoveryTasks = [];

    private UsageIndexService? indexService;
    private SourceContext? currentSource;
    private PanelRefreshRequest? panelRefreshRequest;
    private Task? initialIndexTask;
    private int quotaRefreshRequested;
    private long watcherRecoveryGeneration;
    private long nextSourceGeneration;
    private long quotaBurstStartedAtMilliseconds;
    private volatile bool disposed;

    public DashboardController(
        MainViewModel viewModel,
        Dispatcher dispatcher,
        string appDataDirectory)
    {
        this.viewModel = viewModel;
        this.dispatcher = dispatcher;
        this.appDataDirectory = Path.GetFullPath(appDataDirectory);
        quotaDebounceTimer = new System.Threading.Timer(
            _ => StartQuotaProcessor(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public string? CurrentCodexHome { get; private set; }

    public async Task StartAsync(
        string? requestedCodexHome,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var paths = CodexHomeLocator.Detect(requestedCodexHome);
        var source = await ReplaceIndexServiceAsync(paths, cancellationToken);
        if (source is not null)
        {
            ConfigureWatchers(source);
        }
    }

    public Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        var period = MapPeriod(
            viewModel.SelectedPeriod?.Kind ?? UsagePeriodKind.Today);
        return RefreshPeriodAsync(
            period,
            refreshQuota: true,
            cancellationToken);
    }

    public async Task RefreshPeriodAsync(
        UsagePeriod period,
        bool refreshQuota = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var source = Volatile.Read(ref currentSource);
        if (source is null)
        {
            return;
        }

        var request = BeginPanelRefresh(source, cancellationToken);
        var target = viewModel.GetPeriod(MapPeriod(period));
        await SetPeriodLoadingAsync(target, true, request.Token);

        var gateEntered = false;
        try
        {
            await indexGate.WaitAsync(request.Token);
            gateEntered = true;

            var service = indexService;
            if (service is null || !IsCurrentSource(request.Source))
            {
                return;
            }

            SetRefreshingState(true, indexing: true);
            await Task.Run(
                () => service.RefreshAsync(request.Token),
                request.Token);
            SetRefreshingState(true, indexing: false);

            var snapshot = await Task.Run(
                () => service.QueryPeriodAsync(period, request.Token),
                request.Token);
            await dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrentSource(request.Source))
                    {
                        ApplyPeriod(snapshot);
                    }
                },
                DispatcherPriority.DataBind,
                request.Token);

            if (refreshQuota)
            {
                await CalibrateQuotaAsync(
                    request.Source,
                    resetMonitor: false,
                    updateFullUi: true,
                    clearWhenMissing: false,
                    replaceExisting: false,
                    request.Token);
            }
            else
            {
                await ApplyRateLimitCandidateAsync(
                    request.Source,
                    snapshot.RateLimits,
                    updateFullUi: true,
                    clearWhenMissing: false,
                    replaceExisting: false,
                    request.Token);
            }

            SetLiveState();
        }
        catch (OperationCanceledException) when (
            request.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ShowFriendlyError(exception);
        }
        finally
        {
            if (gateEntered)
            {
                indexGate.Release();
            }

            SetRefreshingState(false, indexing: false);
            await SetPeriodLoadingAsync(target, false, CancellationToken.None);
            CompletePanelRefresh(request);
        }
    }

    public async Task RefreshWeeklyQuotaHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var source = Volatile.Read(ref currentSource);
        if (source is null)
        {
            return;
        }

        var request = BeginPanelRefresh(source, cancellationToken);
        await SetWeeklyQuotaLoadingAsync(
            isLoading: true,
            statusKey: viewModel.HasWeeklyQuotaData
                ? "Loc.SyncingLatestObservation"
                : "Loc.LoadingSevenDayObservations",
            request.Token);

        var gateEntered = false;
        try
        {
            await indexGate.WaitAsync(request.Token);
            gateEntered = true;

            var service = indexService;
            if (service is null || !IsCurrentSource(request.Source))
            {
                return;
            }

            // This full index pass is intentionally tied to the explicit
            // overlay click. The collapsed widget never invokes this path.
            SetRefreshingState(true, indexing: true);
            await Task.Run(
                () => service.RefreshAsync(request.Token),
                request.Token);
            SetRefreshingState(true, indexing: false);

            var timeZone = TimeZoneInfo.Local;
            var localNow = TimeZoneInfo.ConvertTime(
                DateTimeOffset.UtcNow,
                timeZone);
            var today = DateOnly.FromDateTime(localNow.DateTime);
            var firstDate = today.AddDays(-6);
            var afterLastDate = today.AddDays(1);
            var fromInclusive = CreateDateBoundary(firstDate, timeZone);
            var toExclusive = CreateDateBoundary(afterLastDate, timeZone);

            var observations = await Task.Run(
                () => service.QueryWeeklyRateLimitDailyUsageAsync(
                    fromInclusive,
                    toExclusive,
                    request.Token),
                request.Token);
            var rows = CreateWeeklyQuotaRows(
                observations,
                firstDate,
                today);

            await dispatcher.InvokeAsync(
                () =>
                {
                    if (!IsCurrentSource(request.Source))
                    {
                        return;
                    }

                    viewModel.ReplaceWeeklyQuotaDays(rows);
                    var latestObserved = rows
                        .Where(static day => day.IsObserved)
                        .LastOrDefault();
                    if (latestObserved?.ClosingUsedPercent is { } latestUsed)
                    {
                        viewModel.WeeklyQuotaUsedPercent = latestUsed;
                    }

                    if (rows.Any(static day => day.IsObserved))
                    {
                        viewModel.SetWeeklyQuotaStatusMessage(
                            "Loc.DirectObservationUpdatedFormat",
                            DateTimeOffset.Now);
                    }
                    else
                    {
                        viewModel.SetWeeklyQuotaStatusMessage(
                            "Loc.NoWeeklyObservations");
                    }
                },
                DispatcherPriority.DataBind,
                request.Token);
            SetLiveState();
        }
        catch (OperationCanceledException) when (
            request.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            await dispatcher.InvokeAsync(
                () => viewModel.SetWeeklyQuotaStatusMessage(
                    FriendlyMessageKey(exception)),
                DispatcherPriority.Background,
                CancellationToken.None);
        }
        finally
        {
            if (gateEntered)
            {
                indexGate.Release();
            }

            SetRefreshingState(false, indexing: false);
            await SetWeeklyQuotaLoadingAsync(
                isLoading: false,
                statusKey: null,
                CancellationToken.None);
            CompletePanelRefresh(request);
        }
    }

    public void CancelPanelRefresh()
    {
        lock (panelRefreshLock)
        {
            panelRefreshRequest?.Cancel();
        }

        dispatcher.BeginInvoke(
            () =>
            {
                foreach (var period in viewModel.Periods)
                {
                    period.IsLoading = false;
                }

                viewModel.IsWeeklyQuotaLoading = false;
            },
            DispatcherPriority.Background);
    }

    public async Task ChangeCodexHomeAsync(
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var paths = CodexHomeLocator.Detect(codexHome);
        var source = await ReplaceIndexServiceAsync(paths, cancellationToken);
        if (source is not null)
        {
            ConfigureWatchers(source);
        }
    }

    /// <summary>
    /// Rebuilds localized quota labels from the latest in-memory snapshot.
    /// This method intentionally performs no file, index, or database work.
    /// </summary>
    public void ReapplyLocalization()
    {
        ThrowIfDisposed();
        var source = Volatile.Read(ref currentSource);
        if (source is null)
        {
            return;
        }

        RateLimitSnapshot? display;
        lock (rateLimitLock)
        {
            display = source.LatestRateLimits;
        }

        if (dispatcher.CheckAccess())
        {
            ApplyRateLimits(display);
        }
        else
        {
            dispatcher.Invoke(() => ApplyRateLimits(display));
        }
    }

    public async Task RebuildIndexAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CancelPanelRefresh();
        var source = Volatile.Read(ref currentSource);
        if (source is null)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            source.Token,
            cancellationToken);
        var period = MapPeriod(
            viewModel.SelectedPeriod?.Kind ?? UsagePeriodKind.Today);
        var target = viewModel.GetPeriod(MapPeriod(period));
        await SetPeriodLoadingAsync(target, true, linked.Token);

        var gateEntered = false;
        try
        {
            await indexGate.WaitAsync(linked.Token);
            gateEntered = true;
            var service = indexService;
            if (service is null || !IsCurrentSource(source))
            {
                return;
            }

            SetBuildingHistoryState(true);
            SetRefreshingState(true, indexing: true);
            await Task.Run(
                () => service.RebuildIndexAsync(linked.Token),
                linked.Token);

            // RebuildIndexAsync already performs the complete indexing pass.
            // Query the selected period directly instead of immediately
            // enumerating every rollout file for a second RefreshAsync pass.
            SetRefreshingState(true, indexing: false);
            var snapshot = await Task.Run(
                () => service.QueryPeriodAsync(period, linked.Token),
                linked.Token);
            await dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrentSource(source))
                    {
                        ApplyPeriod(snapshot);
                    }
                },
                DispatcherPriority.DataBind,
                linked.Token);
            await CalibrateQuotaAsync(
                source,
                resetMonitor: false,
                updateFullUi: true,
                clearWhenMissing: false,
                replaceExisting: false,
                linked.Token);
            SetLiveState();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ShowFriendlyError(exception);
        }
        finally
        {
            SetBuildingHistoryState(false);
            SetRefreshingState(false, indexing: false);
            if (gateEntered)
            {
                indexGate.Release();
            }

            await SetPeriodLoadingAsync(
                target,
                false,
                CancellationToken.None);
        }
    }

    public Task RecoverAfterResumeAsync(
        CancellationToken cancellationToken = default)
    {
        var source = Volatile.Read(ref currentSource);
        return source is null || disposed
            ? Task.CompletedTask
            : StartTrackedRecovery(
                source,
                cancellationToken,
                releaseRecoverySlot: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetime.Cancel();
        Volatile.Read(ref currentSource)?.Cancel();
        CancelPanelRefresh();
        quotaDebounceTimer.Dispose();
        DisposeWatchers();
        await WaitForQuotaProcessorsAsync();
        await WaitForRecoveryTasksAsync();

        // A startup or Home transition may already be between its awaits.
        // Let it observe cancellation before tearing down its gates, then
        // retain the gate through teardown so no queued transition can start
        // against resources that are being disposed.
        await sourceGate.WaitAsync();
        DisposeWatchers();

        var backgroundIndex = initialIndexTask;
        if (backgroundIndex is not null)
        {
            try
            {
                await backgroundIndex;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await indexGate.WaitAsync();
        try
        {
            if (indexService is not null)
            {
                indexService.ProgressChanged -= IndexServiceOnProgressChanged;
                await indexService.DisposeAsync();
                indexService = null;
            }
        }
        finally
        {
            indexGate.Release();
        }

        await quotaGate.WaitAsync();
        quotaGate.Release();

        lock (panelRefreshLock)
        {
            panelRefreshRequest?.Dispose();
            panelRefreshRequest = null;
        }

        var source = Interlocked.Exchange(ref currentSource, null);
        source?.Dispose();
        foreach (var retiredSource in retiredSources)
        {
            retiredSource.Dispose();
        }

        retiredSources.Clear();
        sourceGate.Dispose();
        indexGate.Dispose();
        quotaGate.Dispose();
        lifetime.Dispose();
    }

    private async Task<SourceContext?> ReplaceIndexServiceAsync(
        CodexHomePaths paths,
        CancellationToken cancellationToken)
    {
        using var entry = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            cancellationToken);
        await sourceGate.WaitAsync(entry.Token);
        UsageIndexService? candidateService = null;
        var candidateCommitted = false;
        try
        {
            ThrowIfDisposed();
            var oldSource = Volatile.Read(ref currentSource);

            // Open the candidate completely before touching the live source.
            // SQLite or filesystem failures must leave the current dashboard,
            // quota watcher, and index service usable.
            candidateService = new UsageIndexService(
                new UsageIndexOptions(
                    paths.HomeDirectory,
                    UsageIndexDatabasePath.ForHome(
                        appDataDirectory,
                        paths.HomeDirectory),
                    TimeZoneInfo.Local,
                    9));
            try
            {
                SetRefreshingState(true, indexing: false);
                await Task.Run(
                    () => candidateService.OpenAsync(entry.Token),
                    entry.Token);
            }
            catch (OperationCanceledException) when (
                entry.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // During startup there is no live source whose status can be
                // preserved. During a Home change, keep the existing healthy
                // UI untouched; App will retain CurrentCodexHome.
                if (oldSource is null)
                {
                    ShowFriendlyError(exception);
                }

                return null;
            }
            finally
            {
                SetRefreshingState(false, indexing: false);
            }

            entry.Token.ThrowIfCancellationRequested();

            // The candidate is now known-good. From this point onward finish
            // the swap under the controller lifetime token so cancellation of
            // an individual caller cannot strand the old source after it has
            // been cancelled.
            DisposeWatchers();
            CancelPanelRefresh();
            oldSource?.Cancel();
            var previousInitialIndex = initialIndexTask;
            initialIndexTask = null;
            if (previousInitialIndex is not null)
            {
                try
                {
                    await previousInitialIndex;
                }
                catch (OperationCanceledException)
                {
                }
            }

            await indexGate.WaitAsync(lifetime.Token);
            var startInitialIndex = false;
            SourceContext? source = null;
            try
            {
                await quotaGate.WaitAsync(lifetime.Token);
                try
                {
                    ResetQuotaQueue();
                    source = new SourceContext(
                        Interlocked.Increment(ref nextSourceGeneration),
                        paths,
                        lifetime.Token);
                    Volatile.Write(ref currentSource, source);
                    CurrentCodexHome = paths.HomeDirectory;
                    Interlocked.Exchange(ref watcherRecoveryGeneration, 0);
                    if (oldSource is not null)
                    {
                        retiredSources.Add(oldSource);
                    }
                }
                finally
                {
                    quotaGate.Release();
                }

                var previousService = indexService;
                if (previousService is not null)
                {
                    previousService.ProgressChanged -=
                        IndexServiceOnProgressChanged;
                }

                candidateService.ProgressChanged +=
                    IndexServiceOnProgressChanged;
                indexService = candidateService;
                candidateCommitted = true;

                if (previousService is not null)
                {
                    await previousService.DisposeAsync();
                }

                await CalibrateQuotaAsync(
                    source,
                    resetMonitor: true,
                    updateFullUi: true,
                    clearWhenMissing: true,
                    replaceExisting: true,
                    lifetime.Token);
                SetLiveState();
                startInitialIndex =
                    !candidateService.HasCompletedInitialIndex;
            }
            catch (OperationCanceledException) when (
                lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                ShowFriendlyError(exception);
            }
            finally
            {
                SetRefreshingState(false, indexing: false);
                indexGate.Release();
            }

            if (startInitialIndex &&
                source is not null &&
                IsCurrentSource(source))
            {
                initialIndexTask = RunInitialIndexAsync(
                    candidateService,
                    source,
                    source.Token);
            }

            return source is not null &&
                   IsCurrentSource(source)
                ? source
                : null;
        }
        finally
        {
            if (!candidateCommitted && candidateService is not null)
            {
                await candidateService.DisposeAsync();
            }

            sourceGate.Release();
        }
    }

    private async Task RunInitialIndexAsync(
        UsageIndexService service,
        SourceContext source,
        CancellationToken cancellationToken)
    {
        var gateEntered = false;
        try
        {
            SetBuildingHistoryState(true);
            SetRefreshingState(true, indexing: true);
            await indexGate.WaitAsync(cancellationToken);
            gateEntered = true;
            if (!ReferenceEquals(service, indexService) ||
                !IsCurrentSource(source))
            {
                return;
            }

            await Task.Run(
                () => service.RefreshAsync(cancellationToken),
                cancellationToken);
            if (IsCurrentSource(source))
            {
                SetLiveState();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ShowFriendlyError(exception);
        }
        finally
        {
            if (gateEntered)
            {
                indexGate.Release();
            }

            SetBuildingHistoryState(false);
            SetRefreshingState(false, indexing: false);
        }
    }

    private PanelRefreshRequest BeginPanelRefresh(
        SourceContext source,
        CancellationToken cancellationToken)
    {
        lock (panelRefreshLock)
        {
            // The superseding request owns only cancellation of its
            // predecessor. The predecessor disposes its own CTS in finally,
            // so it can safely keep using the token it captured at creation.
            panelRefreshRequest?.Cancel();
            panelRefreshRequest = new PanelRefreshRequest(
                source,
                CancellationTokenSource.CreateLinkedTokenSource(
                    lifetime.Token,
                    source.Token,
                    cancellationToken));
            return panelRefreshRequest;
        }
    }

    private void CompletePanelRefresh(PanelRefreshRequest request)
    {
        lock (panelRefreshLock)
        {
            if (ReferenceEquals(panelRefreshRequest, request))
            {
                panelRefreshRequest = null;
            }
        }

        request.Dispose();
    }

    private async Task SetPeriodLoadingAsync(
        UsagePeriodViewModel period,
        bool isLoading,
        CancellationToken cancellationToken)
    {
        try
        {
            await dispatcher.InvokeAsync(
                () => period.IsLoading = isLoading,
                DispatcherPriority.DataBind,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SetWeeklyQuotaLoadingAsync(
        bool isLoading,
        string? statusKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    viewModel.IsWeeklyQuotaLoading = isLoading;
                    if (!string.IsNullOrWhiteSpace(statusKey))
                    {
                        viewModel.SetWeeklyQuotaStatusMessage(statusKey);
                    }
                },
                DispatcherPriority.DataBind,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CalibrateQuotaAsync(
        SourceContext source,
        bool resetMonitor,
        bool updateFullUi,
        bool clearWhenMissing,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentSource(source))
        {
            return;
        }

        await quotaGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrentSource(source))
            {
                return;
            }

            if (resetMonitor)
            {
                source.RateLimitMonitor.Reset();
            }

            var recentFiles = GetRecentSessionFiles(
                source.Paths,
                QuotaCalibrationFileCount);
            var result = await source.RateLimitMonitor.ReadChangedFilesAsync(
                recentFiles,
                cancellationToken);
            await ApplyRateLimitCandidateAsync(
                source,
                result.LatestSnapshot,
                updateFullUi,
                clearWhenMissing,
                replaceExisting,
                cancellationToken);
        }
        finally
        {
            quotaGate.Release();
        }
    }

    private void RequestQuotaRefresh()
    {
        if (disposed)
        {
            return;
        }

        Interlocked.Exchange(ref quotaRefreshRequested, 1);
        var now = Environment.TickCount64;
        var burstStartedAt = Interlocked.CompareExchange(
            ref quotaBurstStartedAtMilliseconds,
            now,
            0);
        if (burstStartedAt == 0)
        {
            burstStartedAt = now;
        }

        var elapsed = Math.Max(0, now - burstStartedAt);
        var delay = Math.Clamp(
            MaximumQuotaDebounceMilliseconds - elapsed,
            0,
            QuietQuotaDebounceMilliseconds);
        ScheduleQuotaRefresh(TimeSpan.FromMilliseconds(delay));
    }

    private void StartQuotaProcessor()
    {
        Task task;
        lock (quotaProcessorLock)
        {
            if (disposed)
            {
                return;
            }

            task = ProcessQuotaQueueAsync();
            quotaProcessorTasks.Add(task);
        }

        _ = task.ContinueWith(
            static (completed, state) =>
                ((DashboardController)state!).CompleteQuotaProcessor(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteQuotaProcessor(Task task)
    {
        // Observe any unexpected failure even though the normal processor
        // handles recoverable IO and cancellation internally.
        _ = task.Exception;
        lock (quotaProcessorLock)
        {
            quotaProcessorTasks.Remove(task);
        }
    }

    private async Task WaitForQuotaProcessorsAsync()
    {
        Task[] pending;
        lock (quotaProcessorLock)
        {
            pending = quotaProcessorTasks.ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Teardown must continue after an already-observed processor
            // failure; the gates below still provide the final barrier.
        }
    }

    private async Task ProcessQuotaQueueAsync()
    {
        if (disposed || lifetime.IsCancellationRequested)
        {
            return;
        }

        var source = Volatile.Read(ref currentSource);
        if (source is null || !IsCurrentSource(source))
        {
            return;
        }

        bool entered;
        try
        {
            entered = await quotaGate.WaitAsync(0, lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!entered)
        {
            ScheduleQuotaRefresh(TimeSpan.FromMilliseconds(250));
            return;
        }

        try
        {
            if (Interlocked.Exchange(ref quotaRefreshRequested, 0) == 0)
            {
                return;
            }

            Interlocked.Exchange(ref quotaBurstStartedAtMilliseconds, 0);
            if (!IsCurrentSource(source))
            {
                return;
            }

            var pathsToRead = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var checkpointSetChanged = false;
            while (quotaChanges.TryDequeue(out var change))
            {
                if (change.SourceGeneration != source.Generation)
                {
                    continue;
                }

                switch (change.Kind)
                {
                    case QuotaFileChangeKind.Renamed:
                        if (!string.IsNullOrWhiteSpace(change.OldPath))
                        {
                            source.RateLimitMonitor.MoveCheckpoint(
                                change.OldPath,
                                change.Path);
                            checkpointSetChanged = true;
                        }

                        pathsToRead.Add(change.Path);
                        break;
                    case QuotaFileChangeKind.Deleted:
                        source.RateLimitMonitor.ForgetFile(change.Path);
                        checkpointSetChanged = true;
                        break;
                    default:
                        pathsToRead.Add(change.Path);
                        break;
                }
            }

            if (pathsToRead.Count == 0 && !checkpointSetChanged)
            {
                return;
            }

            var result = await source.RateLimitMonitor.ReadChangedFilesAsync(
                pathsToRead,
                source.Token);
            if (!IsCurrentSource(source))
            {
                return;
            }

            await ApplyRateLimitCandidateAsync(
                source,
                result.LatestSnapshot,
                updateFullUi: false,
                clearWhenMissing: checkpointSetChanged,
                replaceExisting: checkpointSetChanged,
                source.Token);
        }
        catch (OperationCanceledException) when (
            lifetime.IsCancellationRequested ||
            source.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ShowFriendlyError(exception);
        }
        finally
        {
            quotaGate.Release();
            if (!quotaChanges.IsEmpty)
            {
                RequestQuotaRefresh();
            }
        }
    }

    private async Task ApplyRateLimitCandidateAsync(
        SourceContext source,
        RateLimitSnapshot? candidate,
        bool updateFullUi,
        bool clearWhenMissing,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        RateLimitSnapshot? display;
        double? oldPercent;
        double? newPercent;
        double? oldWeeklyUsedPercent;
        double? newWeeklyUsedPercent;
        lock (rateLimitLock)
        {
            if (!IsCurrentSource(source))
            {
                return;
            }

            oldPercent =
                source.LatestRateLimits?.MostConstrained?.RemainingPercent;
            oldWeeklyUsedPercent =
                FindWeeklyWindow(source.LatestRateLimits)?.UsedPercent;
            if (candidate is not null &&
                (replaceExisting ||
                 source.LatestRateLimits is null ||
                 IsPreferredRateLimit(candidate, source.LatestRateLimits)))
            {
                source.LatestRateLimits = candidate;
            }
            else if (candidate is null && clearWhenMissing)
            {
                source.LatestRateLimits = null;
            }

            display = source.LatestRateLimits;
            newPercent = display?.MostConstrained?.RemainingPercent;
            newWeeklyUsedPercent = FindWeeklyWindow(display)?.UsedPercent;
        }

        if (updateFullUi)
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrentSource(source))
                    {
                        ApplyRateLimits(display);
                    }
                },
                DispatcherPriority.DataBind,
                cancellationToken);
            return;
        }

        if (Nullable.Equals(oldPercent, newPercent) &&
            Nullable.Equals(
                oldWeeklyUsedPercent,
                newWeeklyUsedPercent))
        {
            return;
        }

        await dispatcher.InvokeAsync(
            () =>
            {
                if (IsCurrentSource(source))
                {
                    viewModel.RemainingPercent = newPercent;
                    viewModel.WeeklyQuotaUsedPercent =
                        newWeeklyUsedPercent;
                    viewModel.IsLive = true;
                }
            },
            DispatcherPriority.DataBind,
            cancellationToken);
    }

    private void ApplyPeriod(PeriodSnapshot snapshot)
    {
        var target = viewModel.GetPeriod(MapPeriod(snapshot.Period));
        var rows = snapshot.TopTasks
            .Select(
                (task, index) => new PendingUsageRow(
                    index + 1,
                    task.Title,
                    task.Usage,
                    task.PercentOfPeriod / 100d,
                    task.IsArchived,
                    false))
            .ToList();

        var other = snapshot.Summary.OtherTasksTotal;
        if (snapshot.Summary.TaskCount > 9)
        {
            rows.Add(
                new PendingUsageRow(
                    10,
                    string.Empty,
                    other,
                    snapshot.Summary.Total.TotalTokens == 0
                        ? 0
                        : (double)other.TotalTokens /
                          snapshot.Summary.Total.TotalTokens,
                    false,
                    true));
        }

        var largest = rows.Count == 0
            ? 0L
            : rows.Max(static row => row.Usage.TotalTokens);
        target.ReplaceRankings(
            rows.Select(
                row => new TaskUsageRowViewModel
                {
                    Rank = row.Rank,
                    Title = row.Title,
                    TotalTokens = row.Usage.TotalTokens,
                    InputTokens = row.Usage.InputTokens,
                    CachedInputTokens = row.Usage.CachedInputTokens,
                    OutputTokens = row.Usage.OutputTokens,
                    ReasoningOutputTokens =
                        row.Usage.ReasoningOutputTokens,
                    Share = largest <= 0
                        ? 0
                        : Math.Clamp(
                            (double)row.Usage.TotalTokens / largest,
                            0,
                            1),
                    PeriodShare = Math.Clamp(row.PeriodShare, 0, 1),
                    IsArchived = row.IsArchived,
                    IsAggregate = row.IsAggregate,
                }));

        target.Summary = new UsageSummaryViewModel
        {
            TotalTokens = snapshot.Summary.Total.TotalTokens,
            InputTokens = snapshot.Summary.Total.InputTokens,
            CachedInputTokens = snapshot.Summary.Total.CachedInputTokens,
            OutputTokens = snapshot.Summary.Total.OutputTokens,
            ReasoningOutputTokens =
                snapshot.Summary.Total.ReasoningOutputTokens,
            TaskCount = snapshot.Summary.TaskCount,
            TopNineShare = snapshot.Summary.TopTasksPercent / 100d,
            OtherTokens = other.TotalTokens,
            LastRefresh = snapshot.LastUpdated == DateTimeOffset.MinValue
                ? null
                : snapshot.LastUpdated,
        };
        target.IsLoaded = true;
        target.IsLoading = false;
    }

    private void ApplyRateLimits(RateLimitSnapshot? limits)
    {
        viewModel.ReplaceRateLimitWindows(CreateRateLimitWindows(limits));
        var constrained = limits?.MostConstrained;
        viewModel.RemainingPercent = constrained?.RemainingPercent;
        viewModel.WeeklyQuotaUsedPercent =
            FindWeeklyWindow(limits)?.UsedPercent;

        if (constrained is null)
        {
            viewModel.SetResetMessage("Loc.Unavailable");
            viewModel.SetRateLimitSummaryMessage(
                "Loc.LocalEventsNoLimitWindow");
            return;
        }

        viewModel.SetResetTextLiteral(
            FormatResetCountdown(constrained.ResetsAt));
        var windowName = FormatWindowName(constrained.WindowMinutes);
        var resetAt = constrained.ResetsAt?.ToLocalTime()
            is { } timestamp
            ? LocalizationService.Instance.FormatDateTime(
                timestamp,
                "Loc.DateTimePattern")
            : LocalizationService.Instance.Get("Loc.UnknownTime");
        viewModel.SetRateLimitSummaryMessage(
            "Loc.RateLimitSummaryFormat",
            windowName,
            constrained.RemainingPercent,
            resetAt);
    }

    private static RateLimitWindowSnapshot? FindWeeklyWindow(
        RateLimitSnapshot? limits)
    {
        if (limits is null ||
            !string.Equals(
                limits.LimitId,
                "codex",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (limits.Primary?.WindowMinutes == 10080)
        {
            return limits.Primary;
        }

        return limits.Secondary?.WindowMinutes == 10080
            ? limits.Secondary
            : null;
    }

    private static IReadOnlyList<WeeklyQuotaDayViewModel>
        CreateWeeklyQuotaRows(
            IReadOnlyList<DailyWeeklyRateLimitUsage> observations,
            DateOnly firstDate,
            DateOnly today)
    {
        var byDate = observations.ToDictionary(
            static observation => observation.LocalDate);
        var values = Enumerable.Range(0, 7)
            .Select(
                offset =>
                {
                    var date = firstDate.AddDays(offset);
                    return byDate.TryGetValue(date, out var observation)
                        ? observation
                        : new DailyWeeklyRateLimitUsage(
                            date,
                            null,
                            null,
                            null,
                            0,
                            true);
                })
            .ToArray();

        return values
            .Select(
                value =>
                {
                    var observed = value.ObservationCount > 0;
                    var barHeight = !observed
                        ? 3d
                        : 6d + (32d *
                            Math.Clamp(
                                (value.LastObservedUsedPercent ?? 0d) / 100d,
                                0,
                                1));
                    return new WeeklyQuotaDayViewModel
                    {
                        Date = value.LocalDate,
                        IsToday = value.LocalDate == today,
                        ChangeFromPreviousDayPercent =
                            value.ChangeFromPreviousDayPercentagePoints,
                        ClosingUsedPercent =
                            value.LastObservedUsedPercent,
                        LastObservedAt = value.LastObservedAt,
                        SampleCount = value.ObservationCount,
                        IsObserved = observed,
                        IsPartial = value.IsPartial,
                        BarHeight = barHeight,
                    };
                })
            .ToArray();
    }

    private static DateTimeOffset CreateDateBoundary(
        DateOnly date,
        TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(
            date.ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static IEnumerable<RateLimitWindowViewModel>
        CreateRateLimitWindows(RateLimitSnapshot? limits)
    {
        if (limits?.Primary is { } primary)
        {
            yield return CreateRateLimitWindow(
                LocalizationService.Instance.Get("Loc.PrimaryLimit"),
                primary);
        }

        if (limits?.Secondary is { } secondary)
        {
            yield return CreateRateLimitWindow(
                LocalizationService.Instance.Get("Loc.SecondaryLimit"),
                secondary);
        }
    }

    private static RateLimitWindowViewModel CreateRateLimitWindow(
        string prefix,
        RateLimitWindowSnapshot window)
    {
        var name = LocalizationService.Instance.Format(
            "Loc.RateLimitNameFormat",
            prefix,
            FormatWindowName(window.WindowMinutes));
        var reset = window.ResetsAt?.ToLocalTime()
            is { } timestamp
            ? LocalizationService.Instance.FormatDateTime(
                timestamp,
                "Loc.DateTimePattern")
            : LocalizationService.Instance.Get("Loc.UnknownTime");
        return new RateLimitWindowViewModel
        {
            Name = name,
            RemainingPercent = window.RemainingPercent,
            ResetsAt = window.ResetsAt,
            WindowMinutes = window.WindowMinutes,
            DisplayText = LocalizationService.Instance.Format(
                "Loc.RateLimitDisplayFormat",
                name,
                window.RemainingPercent,
                reset),
        };
    }

    private void ConfigureWatchers(SourceContext source)
    {
        if (!IsCurrentSource(source))
        {
            return;
        }

        lock (watcherLock)
        {
            // The source can change while a recovery callback is waiting for
            // this lock. Revalidate under the same lock that replaces the
            // registrations so an old recovery cannot win after a Home switch.
            if (!IsCurrentSource(source))
            {
                return;
            }

            DisposeWatchersUnsafe();
            if (!Directory.Exists(source.Paths.HomeDirectory))
            {
                return;
            }

            var watcher = new FileSystemWatcher(
                source.Paths.HomeDirectory,
                "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
                               | NotifyFilters.CreationTime,
                InternalBufferSize = 32 * 1024,
                EnableRaisingEvents = false,
            };
            FileSystemEventHandler changed = (_, args) =>
                WatcherOnChanged(source, args);
            RenamedEventHandler renamed = (_, args) =>
                WatcherOnRenamed(source, args);
            ErrorEventHandler error = (_, args) =>
                WatcherOnError(source, args);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += renamed;
            watcher.Error += error;
            watchers.Add(
                new WatcherRegistration(
                    watcher,
                    changed,
                    renamed,
                    error));
            watcher.EnableRaisingEvents = true;
        }
    }

    private void WatcherOnChanged(
        SourceContext source,
        FileSystemEventArgs e)
    {
        if (!IsCurrentSource(source) ||
            !IsSessionLog(source.Paths, e.FullPath))
        {
            return;
        }

        quotaChanges.Enqueue(
            new QuotaFileChange(
                e.FullPath,
                null,
                e.ChangeType == WatcherChangeTypes.Deleted
                    ? QuotaFileChangeKind.Deleted
                    : QuotaFileChangeKind.Changed,
                source.Generation));
        RequestQuotaRefresh();
    }

    private void WatcherOnRenamed(
        SourceContext source,
        RenamedEventArgs e)
    {
        if (!IsCurrentSource(source))
        {
            return;
        }

        var oldIsSession = IsSessionLog(source.Paths, e.OldFullPath);
        var newIsSession = IsSessionLog(source.Paths, e.FullPath);
        if (!oldIsSession && !newIsSession)
        {
            return;
        }

        quotaChanges.Enqueue(
            new QuotaFileChange(
                newIsSession ? e.FullPath : e.OldFullPath,
                oldIsSession ? e.OldFullPath : null,
                newIsSession
                    ? QuotaFileChangeKind.Renamed
                    : QuotaFileChangeKind.Deleted,
                source.Generation));
        RequestQuotaRefresh();
    }

    private void WatcherOnError(
        SourceContext source,
        ErrorEventArgs e)
    {
        if (!IsCurrentSource(source) ||
            Interlocked.CompareExchange(
                ref watcherRecoveryGeneration,
                source.Generation,
                0) != 0)
        {
            return;
        }

        _ = StartTrackedRecovery(
            source,
            source.Token,
            releaseRecoverySlot: true);
    }

    private Task StartTrackedRecovery(
        SourceContext source,
        CancellationToken cancellationToken,
        bool releaseRecoverySlot)
    {
        TaskCompletionSource completion;
        lock (recoveryTaskLock)
        {
            if (disposed)
            {
                return Task.CompletedTask;
            }

            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            recoveryTasks.Add(completion.Task);
        }

        _ = RunTrackedRecoveryAsync(
            completion,
            source,
            cancellationToken,
            releaseRecoverySlot);
        return completion.Task;
    }

    private async Task RunTrackedRecoveryAsync(
        TaskCompletionSource completion,
        SourceContext source,
        CancellationToken cancellationToken,
        bool releaseRecoverySlot)
    {
        try
        {
            await RecoverWatchersAndQuotaAsync(
                source,
                cancellationToken,
                releaseRecoverySlot);
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (recoveryTaskLock)
            {
                recoveryTasks.Remove(completion.Task);
            }
        }
    }

    private async Task WaitForRecoveryTasksAsync()
    {
        Task[] pending;
        lock (recoveryTaskLock)
        {
            pending = recoveryTasks.ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Recovery already maps expected IO failures to the friendly UI;
            // shutdown must continue for any unexpected failure.
        }
    }

    private async Task RecoverWatchersAndQuotaAsync(
        SourceContext source,
        CancellationToken cancellationToken,
        bool releaseRecoverySlot)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            source.Token,
            cancellationToken);
        try
        {
            if (!IsCurrentSource(source))
            {
                return;
            }

            ConfigureWatchers(source);
            await CalibrateQuotaAsync(
                source,
                resetMonitor: true,
                updateFullUi: false,
                clearWhenMissing: false,
                replaceExisting: true,
                linked.Token);
            if (IsCurrentSource(source))
            {
                SetLiveState();
            }
        }
        catch (OperationCanceledException) when (
            linked.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ShowFriendlyError(exception);
        }
        finally
        {
            if (releaseRecoverySlot)
            {
                Interlocked.CompareExchange(
                    ref watcherRecoveryGeneration,
                    0,
                    source.Generation);
            }
        }
    }

    private void DisposeWatchers()
    {
        lock (watcherLock)
        {
            DisposeWatchersUnsafe();
        }
    }

    private void DisposeWatchersUnsafe()
    {
        foreach (var registration in watchers)
        {
            var watcher = registration.Watcher;
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= registration.Changed;
            watcher.Created -= registration.Changed;
            watcher.Deleted -= registration.Changed;
            watcher.Renamed -= registration.Renamed;
            watcher.Error -= registration.Error;
            watcher.Dispose();
        }

        watchers.Clear();
    }

    private static bool IsSessionLog(CodexHomePaths paths, string path)
    {
        if (!string.Equals(
                Path.GetExtension(path),
                ".jsonl",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsWithinDirectory(path, paths.SessionsDirectory) ||
               IsWithinDirectory(path, paths.ArchivedSessionsDirectory);
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static IReadOnlyList<string> GetRecentSessionFiles(
        CodexHomePaths paths,
        int maximumFiles)
    {
        var files = new Dictionary<string, FileInfo>(
            StringComparer.OrdinalIgnoreCase);
        AddRecentFiles(files, paths.SessionsDirectory);
        AddRecentFiles(files, paths.ArchivedSessionsDirectory);
        return files.Values
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenByDescending(static file => file.Length)
            .Take(maximumFiles)
            .Select(static file => file.FullName)
            .ToArray();
    }

    private static void AddRecentFiles(
        IDictionary<string, FileInfo> files,
        string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         "*.jsonl",
                         SearchOption.AllDirectories))
            {
                try
                {
                    var file = new FileInfo(path);
                    files[file.FullName] = file;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ResetQuotaQueue()
    {
        while (quotaChanges.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref quotaRefreshRequested, 0);
        Interlocked.Exchange(ref quotaBurstStartedAtMilliseconds, 0);
        try
        {
            quotaDebounceTimer.Change(
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ScheduleQuotaRefresh(TimeSpan delay)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            quotaDebounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void IndexServiceOnProgressChanged(
        object? sender,
        IndexProgressChangedEventArgs e)
    {
        dispatcher.BeginInvoke(
            () =>
            {
                viewModel.IsIndexing = !e.IsComplete;
                viewModel.IndexProgress = e.Progress;
            },
            DispatcherPriority.Background);
    }

    private void SetRefreshingState(bool refreshing, bool indexing)
    {
        dispatcher.BeginInvoke(
            () =>
            {
                viewModel.IsRefreshing = refreshing;
                if (!refreshing || indexing)
                {
                    viewModel.IsIndexing = refreshing && indexing;
                }
            },
            DispatcherPriority.Background);
    }

    private void SetLiveState()
    {
        dispatcher.BeginInvoke(
            () => viewModel.IsLive = true,
            DispatcherPriority.Background);
    }

    private void SetBuildingHistoryState(bool isBuilding)
    {
        dispatcher.BeginInvoke(
            () => viewModel.IsBuildingHistory = isBuilding,
            DispatcherPriority.Background);
    }

    private void ShowFriendlyError(Exception exception)
    {
        dispatcher.BeginInvoke(
            () =>
            {
                viewModel.IsLive = false;
                viewModel.IsRefreshing = false;
                var key = FriendlyMessageKey(exception);
                viewModel.SetResetMessage(key);
                viewModel.SetRateLimitSummaryMessage(key);
            },
            DispatcherPriority.Background);
    }

    private static UsagePeriodKind MapPeriod(UsagePeriod period) =>
        period switch
        {
            UsagePeriod.Today => UsagePeriodKind.Today,
            UsagePeriod.Last7Days => UsagePeriodKind.LastSevenDays,
            UsagePeriod.Month => UsagePeriodKind.CurrentMonth,
            _ => UsagePeriodKind.AllTime,
        };

    private static UsagePeriod MapPeriod(UsagePeriodKind period) =>
        period switch
        {
            UsagePeriodKind.Today => UsagePeriod.Today,
            UsagePeriodKind.LastSevenDays => UsagePeriod.Last7Days,
            UsagePeriodKind.CurrentMonth => UsagePeriod.Month,
            _ => UsagePeriod.All,
        };

    private static string FormatWindowName(int? minutes)
    {
        return minutes switch
        {
            > 0 and var value when value % 1440 == 0 =>
                LocalizationService.Instance.Format(
                    "Loc.WindowDayFormat",
                    value / 1440),
            > 0 and var value when value % 60 == 0 =>
                LocalizationService.Instance.Format(
                    "Loc.WindowHourFormat",
                    value / 60),
            > 0 and var value =>
                LocalizationService.Instance.Format(
                    "Loc.WindowMinuteFormat",
                    value),
            _ => LocalizationService.Instance.Get("Loc.WindowGeneric"),
        };
    }

    private static string FormatResetCountdown(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return LocalizationService.Instance.Get("Loc.ResetUnknown");
        }

        var remaining = resetsAt.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return LocalizationService.Instance.Get("Loc.ResetSoon");
        }

        if (remaining.TotalDays >= 1)
        {
            return LocalizationService.Instance.Format(
                "Loc.ResetDaysHoursFormat",
                (int)remaining.TotalDays,
                remaining.Hours);
        }

        if (remaining.TotalHours >= 1)
        {
            return LocalizationService.Instance.Format(
                "Loc.ResetHoursMinutesFormat",
                (int)remaining.TotalHours,
                remaining.Minutes);
        }

        return LocalizationService.Instance.Format(
            "Loc.ResetMinutesFormat",
            Math.Max(1, remaining.Minutes));
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
            : candidate.Timestamp >= current.Timestamp;
    }

    private static string FriendlyMessageKey(Exception exception) =>
        exception switch
        {
            DirectoryNotFoundException =>
                "Loc.ErrorDataDirectoryNotFound",
            UnauthorizedAccessException =>
                "Loc.ErrorDataDirectoryAccess",
            IOException =>
                "Loc.ErrorLogChanging",
            _ => "Loc.ErrorReadUsage",
        };

    private static bool IsRecoverable(Exception exception) =>
        exception is DirectoryNotFoundException
            or UnauthorizedAccessException
            or IOException
            or InvalidDataException
            or Microsoft.Data.Sqlite.SqliteException;

    private bool IsCurrentSource(SourceContext source) =>
        !disposed &&
        !source.Token.IsCancellationRequested &&
        ReferenceEquals(Volatile.Read(ref currentSource), source);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private enum QuotaFileChangeKind
    {
        Changed,
        Renamed,
        Deleted,
    }

    private sealed record QuotaFileChange(
        string Path,
        string? OldPath,
        QuotaFileChangeKind Kind,
        long SourceGeneration);

    private sealed record WatcherRegistration(
        FileSystemWatcher Watcher,
        FileSystemEventHandler Changed,
        RenamedEventHandler Renamed,
        ErrorEventHandler Error);

    private sealed record PendingUsageRow(
        int Rank,
        string Title,
        TokenUsage Usage,
        double PeriodShare,
        bool IsArchived,
        bool IsAggregate);

    private sealed class SourceContext : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private int isDisposed;

        public SourceContext(
            long generation,
            CodexHomePaths paths,
            CancellationToken lifetimeToken)
        {
            Generation = generation;
            Paths = paths;
            cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            Token = cancellation.Token;
        }

        public long Generation { get; }

        public CodexHomePaths Paths { get; }

        public CancellationToken Token { get; }

        public RateLimitTailMonitor RateLimitMonitor { get; } = new();

        public RateLimitSnapshot? LatestRateLimits { get; set; }

        public void Cancel()
        {
            try
            {
                if (Volatile.Read(ref isDisposed) == 0)
                {
                    cancellation.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref isDisposed, 1) == 0)
            {
                cancellation.Dispose();
            }
        }
    }

    private sealed class PanelRefreshRequest : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private int isDisposed;

        public PanelRefreshRequest(
            SourceContext source,
            CancellationTokenSource cancellation)
        {
            Source = source;
            this.cancellation = cancellation;
            Token = cancellation.Token;
        }

        public SourceContext Source { get; }

        public CancellationToken Token { get; }

        public void Cancel()
        {
            try
            {
                if (Volatile.Read(ref isDisposed) == 0)
                {
                    cancellation.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref isDisposed, 1) == 0)
            {
                cancellation.Dispose();
            }
        }
    }
}
