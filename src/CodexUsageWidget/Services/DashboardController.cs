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
    private readonly MonitoringActivityGate monitoringActivity = new();
    private readonly SemaphoreSlim monitoringTransitionGate = new(1, 1);
    private readonly SemaphoreSlim sourceGate = new(1, 1);
    private readonly SemaphoreSlim indexGate = new(1, 1);
    private readonly SemaphoreSlim quotaGate = new(1, 1);
    private readonly System.Threading.Timer quotaDebounceTimer;
    private readonly ConcurrentQueue<QuotaFileChange> quotaChanges = new();
    private readonly object panelRefreshLock = new();
    private readonly object initialIndexLock = new();
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

    public bool IsMonitoringPaused => monitoringActivity.IsPaused;

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
        if (source is null ||
            !monitoringActivity.TryCapture(out var activity))
        {
            return;
        }

        var request = BeginPanelRefresh(
            source,
            activity,
            cancellationToken);
        using var indexRefreshCancellation =
            CreatePanelIndexRefreshCancellation(
                request,
                cancellationToken);
        var indexRefreshToken = indexRefreshCancellation.Token;
        var target = viewModel.GetPeriod(MapPeriod(period));
        await SetPeriodLoadingAsync(target, true, request.Token);

        var gateEntered = false;
        var historyBuildVisible = false;
        try
        {
            // Once a user action starts an index catch-up, let it finish even
            // if the hover panel closes. The panel token still cancels the
            // following query/UI work, while source, display-dormancy, Home
            // switch, caller, and application shutdown tokens can stop IO.
            await indexGate.WaitAsync(indexRefreshToken);
            gateEntered = true;

            if (IsSupersededPanelRefresh(request))
            {
                return;
            }

            var service = indexService;
            if (service is null || !IsCurrentSource(request.Source))
            {
                return;
            }

            historyBuildVisible =
                service.RequiresHistoryBuild || !target.IsLoaded;
            if (historyBuildVisible)
            {
                SetBuildingHistoryState(true);
            }

            SetRefreshingState(true, indexing: true);
            await Task.Run(
                () => service.RefreshAsync(indexRefreshToken),
                indexRefreshToken);
            request.Token.ThrowIfCancellationRequested();
            SetRefreshingState(true, indexing: false);

            var snapshot = await Task.Run(
                () => service.QueryPeriodAsync(period, request.Token),
                request.Token);
            await dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrentSource(request.Source) &&
                        monitoringActivity.IsCurrent(request.Activity))
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
            request.Token.IsCancellationRequested ||
            indexRefreshToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (IsCurrentSource(request.Source) &&
                monitoringActivity.IsCurrent(request.Activity))
            {
                ShowFriendlyError(exception);
            }
        }
        catch (Exception exception)
        {
            LocalDiagnosticLog.TryWrite(
                appDataDirectory,
                "period-refresh",
                exception);
            if (IsCurrentSource(request.Source) &&
                monitoringActivity.IsCurrent(request.Activity))
            {
                ShowFriendlyError(exception);
            }
        }
        finally
        {
            if (gateEntered)
            {
                indexGate.Release();
            }

            SetRefreshingState(false, indexing: false);
            if (historyBuildVisible)
            {
                SetBuildingHistoryState(false);
            }
            await SetPeriodLoadingAsync(target, false, CancellationToken.None);
            CompletePanelRefresh(request);
            ContinueInitialIndexIfIncomplete(
                request.Source,
                request.Activity);
        }
    }

    public async Task RefreshWeeklyQuotaHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var source = Volatile.Read(ref currentSource);
        if (source is null ||
            !monitoringActivity.TryCapture(out var activity))
        {
            return;
        }

        var request = BeginPanelRefresh(
            source,
            activity,
            cancellationToken);
        using var indexRefreshCancellation =
            CreatePanelIndexRefreshCancellation(
                request,
                cancellationToken);
        var indexRefreshToken = indexRefreshCancellation.Token;
        await SetWeeklyQuotaLoadingAsync(
            isLoading: true,
            statusKey: viewModel.HasWeeklyQuotaData
                ? "Loc.SyncingLatestObservation"
                : "Loc.LoadingSevenDayObservations",
            request.Token);

        var gateEntered = false;
        var historyBuildVisible = false;
        try
        {
            // Index persistence follows the same rule as period refreshes:
            // closing the overlay cancels only its query and presentation.
            await indexGate.WaitAsync(indexRefreshToken);
            gateEntered = true;

            if (IsSupersededPanelRefresh(request))
            {
                return;
            }

            var service = indexService;
            if (service is null || !IsCurrentSource(request.Source))
            {
                return;
            }

            // This full index pass is intentionally tied to the explicit
            // overlay click. The collapsed widget never invokes this path.
            historyBuildVisible =
                service.RequiresHistoryBuild ||
                !viewModel.HasWeeklyQuotaData;
            if (historyBuildVisible)
            {
                SetBuildingHistoryState(true);
            }

            SetRefreshingState(true, indexing: true);
            await Task.Run(
                () => service.RefreshAsync(indexRefreshToken),
                indexRefreshToken);
            request.Token.ThrowIfCancellationRequested();
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
                    if (!IsCurrentSource(request.Source) ||
                        !monitoringActivity.IsCurrent(request.Activity))
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
            request.Token.IsCancellationRequested ||
            indexRefreshToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (IsCurrentSource(request.Source) &&
                monitoringActivity.IsCurrent(request.Activity))
            {
                await dispatcher.InvokeAsync(
                    () => viewModel.SetWeeklyQuotaStatusMessage(
                        FriendlyMessageKey(exception)),
                    DispatcherPriority.Background,
                    CancellationToken.None);
            }
        }
        finally
        {
            if (gateEntered)
            {
                indexGate.Release();
            }

            SetRefreshingState(false, indexing: false);
            if (historyBuildVisible)
            {
                SetBuildingHistoryState(false);
            }
            await SetWeeklyQuotaLoadingAsync(
                isLoading: false,
                statusKey: null,
                CancellationToken.None);
            CompletePanelRefresh(request);
            ContinueInitialIndexIfIncomplete(
                request.Source,
                request.Activity);
        }
    }

    public void CancelPanelRefresh()
    {
        PanelRefreshRequest? request;
        lock (panelRefreshLock)
        {
            request = panelRefreshRequest;
        }

        // Cancellation callbacks are allowed to run synchronously. Never
        // invoke them while holding the ownership lock that completion uses.
        request?.Cancel();

        TryBeginInvoke(
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

    public async Task<bool> ChangeCodexHomeAsync(
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var paths = CodexHomeLocator.Detect(codexHome);
        var source = await ReplaceIndexServiceAsync(paths, cancellationToken);
        if (source is not null)
        {
            ConfigureWatchers(source);
            return true;
        }

        return false;
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
            if (!dispatcher.HasShutdownStarted &&
                !dispatcher.HasShutdownFinished)
            {
                ApplyRateLimits(display);
            }
        }
        else
        {
            TryInvoke(() => ApplyRateLimits(display));
        }
    }

    public async Task RebuildIndexAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CancelPanelRefresh();
        var source = Volatile.Read(ref currentSource);
        if (source is null ||
            !monitoringActivity.TryCapture(out var activity))
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            source.Token,
            activity.CancellationToken,
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
                    if (IsCurrentSource(source) &&
                        monitoringActivity.IsCurrent(activity))
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
            if (IsCurrentSource(source) &&
                monitoringActivity.IsCurrent(activity))
            {
                ShowFriendlyError(exception);
            }
        }
        catch (Exception exception)
        {
            LocalDiagnosticLog.TryWrite(
                appDataDirectory,
                "index-rebuild",
                exception);
            if (IsCurrentSource(source) &&
                monitoringActivity.IsCurrent(activity))
            {
                ShowFriendlyError(exception);
            }
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
        return source is null ||
               disposed ||
               !monitoringActivity.TryCapture(out var activity)
            ? Task.CompletedTask
            : StartTrackedRecovery(
                source,
                activity,
                releaseRecoverySlot: false,
                cancellationToken);
    }

    /// <summary>
    /// Enters application-managed dormancy. Existing page work, quota tail
    /// reads, watcher recovery, and first-run indexing are cancelled at safe
    /// cancellation points before this method completes.
    /// </summary>
    public async Task PauseMonitoringAsync(
        CancellationToken cancellationToken = default)
    {
        await monitoringTransitionGate.WaitAsync(cancellationToken);
        try
        {
            if (disposed || !monitoringActivity.Pause())
            {
                return;
            }

            CancelPanelRefresh();
            ResetQuotaQueue();
            DisposeWatchers();
            Interlocked.Exchange(ref watcherRecoveryGeneration, 0);
            SetBuildingHistoryState(false);
            SetRefreshingState(false, indexing: false);

            await WaitForQuotaProcessorsAsync();
            await WaitForRecoveryTasksAsync();
            var backgroundIndex = GetInitialIndexTask();
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

            await quotaGate.WaitAsync(CancellationToken.None);
            quotaGate.Release();
            await indexGate.WaitAsync(CancellationToken.None);
            indexGate.Release();

            // A startup or Home switch may have entered before dormancy was
            // requested and performs its candidate open/calibration under
            // sourceGate. Waiting for that transition provides the final
            // barrier: once PauseMonitoringAsync returns, no source swap can
            // still be reading logs or opening the index in the background.
            await sourceGate.WaitAsync(CancellationToken.None);
            sourceGate.Release();
            monitoringActivity.ReleaseRetiredCancellations();
        }
        finally
        {
            monitoringTransitionGate.Release();
        }
    }

    /// <summary>
    /// Leaves dormancy and performs the same bounded watcher/quota recovery
    /// used after a system resume. Period statistics remain user-triggered.
    /// </summary>
    public async Task ResumeMonitoringAsync(
        CancellationToken cancellationToken = default)
    {
        await monitoringTransitionGate.WaitAsync(cancellationToken);
        try
        {
            if (disposed || !monitoringActivity.Resume())
            {
                return;
            }

            await RecoverAfterResumeAsync(cancellationToken);
        }
        finally
        {
            monitoringTransitionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await monitoringTransitionGate.WaitAsync();
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            monitoringActivity.Dispose();
            try
            {
                lifetime.Cancel();
            }
            catch (AggregateException exception)
            {
                // Every callback has already been invoked. Log the callback
                // failure and continue releasing the controller resources.
                LocalDiagnosticLog.TryWrite(
                    appDataDirectory,
                    "dashboard-lifetime-cancel",
                    exception);
            }
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

            var backgroundIndex = GetInitialIndexTask();
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
        finally
        {
            monitoringTransitionGate.Release();
        }
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
            var previousInitialIndex = TakeInitialIndexTask();
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

            if (source is not null &&
                IsCurrentSource(source))
            {
                EnsureInitialIndexRunning(
                    candidateService,
                    source);
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
        MonitoringActivityLease activity)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            source.Token,
            activity.CancellationToken);
        var cancellationToken = linked.Token;
        var gateEntered = false;
        try
        {
            SetBuildingHistoryState(true);
            SetRefreshingState(true, indexing: true);
            await indexGate.WaitAsync(cancellationToken);
            gateEntered = true;
            if (!ReferenceEquals(service, indexService) ||
                !IsCurrentSource(source) ||
                !monitoringActivity.IsCurrent(activity))
            {
                return;
            }

            await Task.Run(
                () => service.RefreshAsync(cancellationToken),
                cancellationToken);
            if (IsCurrentSource(source) &&
                monitoringActivity.IsCurrent(activity))
            {
                SetLiveState();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (IsCurrentSource(source) &&
                monitoringActivity.IsCurrent(activity))
            {
                ShowFriendlyError(exception);
            }
        }
        catch (Exception exception)
        {
            LocalDiagnosticLog.TryWrite(
                appDataDirectory,
                "initial-index",
                exception);
            if (IsCurrentSource(source) &&
                monitoringActivity.IsCurrent(activity))
            {
                ShowFriendlyError(exception);
            }
        }
        finally
        {
            if (gateEntered)
            {
                indexGate.Release();
            }

            if (IsCurrentSource(source) &&
                monitoringActivity.IsCurrent(activity))
            {
                SetBuildingHistoryState(false);
                SetRefreshingState(false, indexing: false);
            }
        }
    }

    private void EnsureInitialIndexRunning(
        UsageIndexService service,
        SourceContext source)
    {
        if (service.HasCompletedInitialIndex ||
            !IsCurrentSource(source) ||
            !monitoringActivity.TryCapture(out var activity))
        {
            return;
        }

        lock (initialIndexLock)
        {
            if (initialIndexTask is { IsCompleted: false } ||
                service.HasCompletedInitialIndex ||
                !IsCurrentSource(source) ||
                !monitoringActivity.IsCurrent(activity))
            {
                return;
            }

            initialIndexTask = RunInitialIndexAsync(
                service,
                source,
                activity);
        }
    }

    private void ContinueInitialIndexIfIncomplete(
        SourceContext source,
        MonitoringActivityLease activity)
    {
        var service = indexService;
        if (service is null ||
            service.HasCompletedInitialIndex ||
            !IsCurrentSource(source) ||
            !monitoringActivity.IsCurrent(activity))
        {
            return;
        }

        // A statistics request may have started a one-time index migration and
        // then been cancelled by collapse, a superseding tab click, or caller
        // cancellation. Once the old index has been cleared, the remaining
        // rebuild belongs to the source lifecycle and must outlive the panel.
        EnsureInitialIndexRunning(service, source);
    }

    private Task? GetInitialIndexTask()
    {
        lock (initialIndexLock)
        {
            return initialIndexTask;
        }
    }

    private Task? TakeInitialIndexTask()
    {
        lock (initialIndexLock)
        {
            var task = initialIndexTask;
            initialIndexTask = null;
            return task;
        }
    }

    private PanelRefreshRequest BeginPanelRefresh(
        SourceContext source,
        MonitoringActivityLease activity,
        CancellationToken cancellationToken)
    {
        var next = new PanelRefreshRequest(
            source,
            activity,
            CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.Token,
                source.Token,
                activity.CancellationToken,
                cancellationToken));
        PanelRefreshRequest? previous;
        lock (panelRefreshLock)
        {
            previous = panelRefreshRequest;
            panelRefreshRequest = next;
        }

        // The predecessor owns disposal of its CTS in finally. Cancel it only
        // after publishing the new owner and releasing the completion lock.
        previous?.Cancel();
        return next;
    }

    private CancellationTokenSource CreatePanelIndexRefreshCancellation(
        PanelRefreshRequest request,
        CancellationToken callerToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            request.Source.Token,
            request.Activity.CancellationToken,
            callerToken);

    private bool IsSupersededPanelRefresh(PanelRefreshRequest request)
    {
        lock (panelRefreshLock)
        {
            // Collapse keeps the same cancelled request published until its
            // finally block, so its already-triggered index pass continues.
            // A newer tab/refresh request publishes a different owner and is
            // responsible for the catch-up instead.
            return request.Token.IsCancellationRequested &&
                   !ReferenceEquals(panelRefreshRequest, request);
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

    private void RequestQuotaRefresh(
        MonitoringActivityLease activity)
    {
        if (disposed || !monitoringActivity.IsCurrent(activity))
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
        ScheduleQuotaRefresh(
            TimeSpan.FromMilliseconds(delay),
            activity);
    }

    private void StartQuotaProcessor()
    {
        Task task;
        lock (quotaProcessorLock)
        {
            if (disposed || monitoringActivity.IsPaused)
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
        if (source is null ||
            !IsCurrentSource(source) ||
            !monitoringActivity.TryCapture(out var activity))
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            source.Token,
            activity.CancellationToken);
        var cancellationToken = linked.Token;
        bool entered;
        try
        {
            entered = await quotaGate.WaitAsync(0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!entered)
        {
            ScheduleQuotaRefresh(
                TimeSpan.FromMilliseconds(250),
                activity);
            return;
        }

        try
        {
            if (Interlocked.Exchange(ref quotaRefreshRequested, 0) == 0)
            {
                return;
            }

            Interlocked.Exchange(ref quotaBurstStartedAtMilliseconds, 0);
            if (!IsCurrentSource(source) ||
                !monitoringActivity.IsCurrent(activity))
            {
                return;
            }

            var pathsToRead = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var checkpointSetChanged = false;
            while (quotaChanges.TryDequeue(out var change))
            {
                if (change.SourceGeneration != source.Generation ||
                    change.ActivityGeneration != activity.Generation)
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
                cancellationToken);
            if (!IsCurrentSource(source) ||
                !monitoringActivity.IsCurrent(activity))
            {
                return;
            }

            await ApplyRateLimitCandidateAsync(
                source,
                result.LatestSnapshot,
                updateFullUi: false,
                clearWhenMissing: checkpointSetChanged,
                replaceExisting: checkpointSetChanged,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            lifetime.IsCancellationRequested ||
            source.Token.IsCancellationRequested ||
            activity.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (IsCurrentSource(source) &&
                monitoringActivity.IsCurrent(activity))
            {
                ShowFriendlyError(exception);
            }
        }
        catch (Exception exception)
        {
            LocalDiagnosticLog.TryWrite(
                appDataDirectory,
                "quota-tail-refresh",
                exception);
            if (IsCurrentSource(source) &&
                monitoringActivity.IsCurrent(activity))
            {
                ShowFriendlyError(exception);
            }
        }
        finally
        {
            quotaGate.Release();
            if (!quotaChanges.IsEmpty &&
                monitoringActivity.IsCurrent(activity))
            {
                RequestQuotaRefresh(activity);
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

    private static WeeklyQuotaDayViewModel[]
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
                                (value.ConsumedPercentagePoints ?? 0d) / 100d,
                                0,
                                1));
                    return new WeeklyQuotaDayViewModel
                    {
                        Date = value.LocalDate,
                        IsToday = value.LocalDate == today,
                        ChangeFromPreviousDayPercent =
                            value.ChangeFromPreviousDayPercentagePoints,
                        DailyConsumedPercent =
                            value.ConsumedPercentagePoints,
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
        if (!monitoringActivity.TryCapture(out var activity))
        {
            return;
        }

        ConfigureWatchers(source, activity);
    }

    private void ConfigureWatchers(
        SourceContext source,
        MonitoringActivityLease activity)
    {
        if (!IsCurrentSource(source) ||
            !monitoringActivity.IsCurrent(activity))
        {
            return;
        }

        lock (watcherLock)
        {
            // The source can change while a recovery callback is waiting for
            // this lock. Revalidate under the same lock that replaces the
            // registrations so an old recovery cannot win after a Home switch.
            if (!IsCurrentSource(source) ||
                !monitoringActivity.IsCurrent(activity))
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
                WatcherOnChanged(source, activity, args);
            RenamedEventHandler renamed = (_, args) =>
                WatcherOnRenamed(source, activity, args);
            ErrorEventHandler error = (_, args) =>
                WatcherOnError(source, activity, args);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += renamed;
            watcher.Error += error;
            var registration = new WatcherRegistration(
                watcher,
                changed,
                renamed,
                error);
            watchers.Add(registration);
            try
            {
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                watchers.Remove(registration);
                DisposeWatcherRegistration(registration);
                throw;
            }
        }
    }

    private void WatcherOnChanged(
        SourceContext source,
        MonitoringActivityLease activity,
        FileSystemEventArgs e)
    {
        if (!IsCurrentSource(source) ||
            !monitoringActivity.IsCurrent(activity) ||
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
                source.Generation,
                activity.Generation));
        RequestQuotaRefresh(activity);
    }

    private void WatcherOnRenamed(
        SourceContext source,
        MonitoringActivityLease activity,
        RenamedEventArgs e)
    {
        if (!IsCurrentSource(source) ||
            !monitoringActivity.IsCurrent(activity))
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
                source.Generation,
                activity.Generation));
        RequestQuotaRefresh(activity);
    }

    private void WatcherOnError(
        SourceContext source,
        MonitoringActivityLease activity,
        ErrorEventArgs e)
    {
        if (!IsCurrentSource(source) ||
            !monitoringActivity.IsCurrent(activity) ||
            Interlocked.CompareExchange(
                ref watcherRecoveryGeneration,
                activity.Generation,
                0) != 0)
        {
            return;
        }

        _ = StartTrackedRecovery(
            source,
            activity,
            releaseRecoverySlot: true,
            source.Token);
    }

    private Task StartTrackedRecovery(
        SourceContext source,
        MonitoringActivityLease activity,
        bool releaseRecoverySlot,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource completion;
        lock (recoveryTaskLock)
        {
            if (disposed ||
                !IsCurrentSource(source) ||
                !monitoringActivity.IsCurrent(activity))
            {
                return Task.CompletedTask;
            }

            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            recoveryTasks.Add(completion.Task);
        }

        // A FileSystemWatcher error can enter here on the watcher's own
        // callback thread. Never dispose/recreate that watcher inline on the
        // same stack; the tracked task also gives teardown a stable barrier.
        _ = Task.Run(
            () => RunTrackedRecoveryAsync(
                completion,
                source,
                activity,
                releaseRecoverySlot,
                cancellationToken),
            CancellationToken.None);
        return completion.Task;
    }

    private async Task RunTrackedRecoveryAsync(
        TaskCompletionSource completion,
        SourceContext source,
        MonitoringActivityLease activity,
        bool releaseRecoverySlot,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecoverWatchersAndQuotaAsync(
                source,
                activity,
                releaseRecoverySlot,
                cancellationToken);
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
        MonitoringActivityLease activity,
        bool releaseRecoverySlot,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            source.Token,
            activity.CancellationToken,
            cancellationToken);
        try
        {
            if (!IsCurrentSource(source) ||
                !monitoringActivity.IsCurrent(activity))
            {
                return;
            }

            ResetQuotaQueue();
            ConfigureWatchers(source, activity);
            await CalibrateQuotaAsync(
                source,
                resetMonitor: true,
                updateFullUi: false,
                clearWhenMissing: false,
                replaceExisting: true,
                linked.Token);
            if (IsCurrentSource(source) &&
                monitoringActivity.IsCurrent(activity))
            {
                SetLiveState();
                var service = indexService;
                if (service is not null)
                {
                    EnsureInitialIndexRunning(service, source);
                }
            }
        }
        catch (OperationCanceledException) when (
            linked.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (IsCurrentSource(source) &&
                monitoringActivity.IsCurrent(activity))
            {
                ShowFriendlyError(exception);
            }
        }
        catch (Exception exception)
        {
            LocalDiagnosticLog.TryWrite(
                appDataDirectory,
                "watcher-recovery",
                exception);
            if (IsCurrentSource(source) &&
                monitoringActivity.IsCurrent(activity))
            {
                ShowFriendlyError(exception);
            }
        }
        finally
        {
            if (releaseRecoverySlot)
            {
                Interlocked.CompareExchange(
                    ref watcherRecoveryGeneration,
                    0,
                    activity.Generation);
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
            DisposeWatcherRegistration(registration);
        }

        watchers.Clear();
    }

    private static void DisposeWatcherRegistration(
        WatcherRegistration registration)
    {
        var watcher = registration.Watcher;
        try
        {
            watcher.EnableRaisingEvents = false;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        watcher.Changed -= registration.Changed;
        watcher.Created -= registration.Changed;
        watcher.Deleted -= registration.Changed;
        watcher.Renamed -= registration.Renamed;
        watcher.Error -= registration.Error;
        watcher.Dispose();
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

    private static string[] GetRecentSessionFiles(
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
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         "*.jsonl",
                         options))
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

    private void ScheduleQuotaRefresh(
        TimeSpan delay,
        MonitoringActivityLease activity)
    {
        if (disposed || !monitoringActivity.IsCurrent(activity))
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
        if (disposed ||
            sender is not UsageIndexService service ||
            !ReferenceEquals(service, indexService) ||
            !monitoringActivity.TryCapture(out var activity))
        {
            return;
        }

        TryBeginInvoke(
            () =>
            {
                if (disposed ||
                    !ReferenceEquals(service, indexService) ||
                    !monitoringActivity.IsCurrent(activity))
                {
                    return;
                }

                viewModel.IsIndexing = !e.IsTerminal;
                viewModel.IndexProgress = e.Progress;
                viewModel.IndexProgressStage = e.Stage;
            },
            DispatcherPriority.Background);
    }

    private void SetRefreshingState(bool refreshing, bool indexing)
    {
        TryBeginInvoke(
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
        TryBeginInvoke(
            () => viewModel.IsLive = true,
            DispatcherPriority.Background);
    }

    private void SetBuildingHistoryState(bool isBuilding)
    {
        TryBeginInvoke(
            () =>
            {
                if (isBuilding)
                {
                    // Reset the prior terminal/partial state before making the
                    // notice visible. Keeping these changes in one dispatcher
                    // callback prevents a one-frame flash of stale progress.
                    viewModel.IndexProgress = 0;
                    viewModel.IndexProgressStage =
                        IndexProgressStage.Preparing;
                }

                viewModel.IsBuildingHistory = isBuilding;
            },
            DispatcherPriority.Background);
    }

    private void ShowFriendlyError(Exception exception)
    {
        TryBeginInvoke(
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

    private bool TryBeginInvoke(
        Action callback,
        DispatcherPriority priority)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            dispatcher.BeginInvoke(callback, priority);
            return true;
        }
        catch (InvalidOperationException) when (
            disposed ||
            dispatcher.HasShutdownStarted ||
            dispatcher.HasShutdownFinished)
        {
            // UI-only notifications are irrelevant once shutdown begins.
            // Containing this race keeps a late progress callback from
            // interrupting index completion or disposal.
            return false;
        }
    }

    private bool TryInvoke(Action callback)
    {
        if (disposed ||
            dispatcher.HasShutdownStarted ||
            dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                callback();
            }
            else
            {
                dispatcher.Invoke(callback);
            }

            return true;
        }
        catch (InvalidOperationException) when (
            disposed ||
            dispatcher.HasShutdownStarted ||
            dispatcher.HasShutdownFinished)
        {
            return false;
        }
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
            System.Security.SecurityException =>
                "Loc.ErrorDataDirectoryAccess",
            IOException =>
                "Loc.ErrorLogChanging",
            _ => "Loc.ErrorReadUsage",
        };

    private static bool IsRecoverable(Exception exception) =>
        exception is DirectoryNotFoundException
            or UnauthorizedAccessException
            or System.Security.SecurityException
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
        long SourceGeneration,
        long ActivityGeneration);

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
            catch (AggregateException)
            {
                // Cancellation still reached every registered callback.
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
            MonitoringActivityLease activity,
            CancellationTokenSource cancellation)
        {
            Source = source;
            Activity = activity;
            this.cancellation = cancellation;
            Token = cancellation.Token;
        }

        public SourceContext Source { get; }

        public MonitoringActivityLease Activity { get; }

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
            catch (AggregateException)
            {
                // Cancellation still reached every registered callback.
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
