using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CodexUsageWidget.Core;
using CodexUsageWidget.Services;
using CodexUsageWidget.ViewModels;
using Microsoft.Win32;

namespace CodexUsageWidget;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim settingsDialogGate = new(1, 1);
    private SingleInstanceGuard? singleInstance;
    private SettingsService? settingsService;
    private StartupRegistrationService? startupRegistration;
    private TrayIconService? trayIcon;
    private DashboardController? dashboard;
    private MainViewModel? viewModel;
    private MainWindow? mainWindow;
    private DispatcherTimer? settingsSaveTimer;
    private Task? dashboardStartTask;
    private AppSettings settings = new();
    private bool exitRequested;
    private bool cleanupComplete;
    private bool systemEventsSubscribed;
    private bool settingsWriteWarningShown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        LocalizationService.Instance.Apply(AppLanguageMode.System);

        if (!SingleInstanceGuard.TryAcquire(out singleInstance))
        {
            System.Windows.MessageBox.Show(
                LocalizationService.Instance.Get("Loc.AppAlreadyRunning"),
                LocalizationService.Instance.Get("Loc.AppName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            // No window, tray icon, index, or settings writer exists yet.
            // WPF can otherwise enter a headless message loop when Shutdown
            // is requested before startup completes, so terminate only this
            // duplicate process deterministically after the notice closes.
            Environment.Exit(0);
            return;
        }

        try
        {
            settingsService = new SettingsService();
            startupRegistration = new StartupRegistrationService();
            settings = await settingsService.LoadAsync(lifetime.Token);
            settings.StartWithWindows = SafeReadStartupState();
            LocalizationService.Instance.Apply(settings.LanguageMode);

            viewModel = CreateViewModel(settings);
            mainWindow = new MainWindow();
            mainWindow.SetViewModel(viewModel);
            mainWindow.ApplyLocalization(LocalizationService.Instance.Culture);
            mainWindow.ApplyAppearance(
                ThemeService.ShouldUseLightTheme(settings.ThemeMode));
            mainWindow.SetGlassTransparencyPercent(
                settings.GlassTransparencyPercent);
            mainWindow.SetCollapsedMode(
                CollapsedWidgetModePolicy.Parse(settings.CollapsedMode));
            mainWindow.SetAutoCollapse(
                settings.AutoCollapse,
                settings.AutoCollapseDelayMs);
            ApplyWindowPosition(mainWindow, settings);
            WireWindowEvents(mainWindow, viewModel);

            settingsSaveTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(700),
                DispatcherPriority.Background,
                SettingsSaveTimerOnTick,
                Dispatcher);
            settingsSaveTimer.Stop();

            dashboard = new DashboardController(
                viewModel,
                Dispatcher,
                settingsService.AppDataDirectory);
            trayIcon = CreateTrayIcon();
            trayIcon.SetStartupEnabled(settings.StartWithWindows);
            SystemEvents.UserPreferenceChanged +=
                SystemEventsOnUserPreferenceChanged;
            SystemEvents.PowerModeChanged +=
                SystemEventsOnPowerModeChanged;
            systemEventsSubscribed = true;

            mainWindow.Show();
            trayIcon.SetWindowVisible(true);
            if (settings.IsPinned)
            {
                mainWindow.Expand();
            }
            else
            {
                mainWindow.Collapse(force: true);
            }

            dashboardStartTask = StartDashboardAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            Shutdown();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                LocalizationService.Instance.Format(
                    "Loc.StartupFailedFormat",
                    exception.Message),
                LocalizationService.Instance.Get("Loc.AppName"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await ExitApplicationAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (systemEventsSubscribed)
        {
            SystemEvents.UserPreferenceChanged -=
                SystemEventsOnUserPreferenceChanged;
            SystemEvents.PowerModeChanged -=
                SystemEventsOnPowerModeChanged;
            systemEventsSubscribed = false;
        }

        if (!cleanupComplete)
        {
            lifetime.Cancel();
            trayIcon?.Dispose();
            singleInstance?.Dispose();
            cleanupComplete = true;
        }

        settingsSaveTimer?.Stop();
        settingsDialogGate.Dispose();
        lifetime.Dispose();
        base.OnExit(e);
    }

    private MainViewModel CreateViewModel(AppSettings source)
    {
        var model = new MainViewModel
        {
            IsAlwaysOnTop = source.AlwaysOnTop,
            AutoCollapseEnabled = source.AutoCollapse,
            AutoCollapseDelayMilliseconds = source.AutoCollapseDelayMs,
            IsPinned = source.IsPinned,
            IsExpanded = source.IsPinned,
        };
        model.SelectedPeriod = model.GetPeriod(ParsePeriod(source.SelectedPeriod));
        return model;
    }

    private void WireWindowEvents(MainWindow window, MainViewModel model)
    {
        window.RefreshRequested += (_, _) => _ = RefreshDashboardAsync();
        window.HoverExpanded += (_, _) =>
            _ = RefreshPeriodFromUiAsync(
                UsagePeriod.Today,
                refreshQuota: true);
        window.PeriodRefreshRequested += (_, args) =>
            _ = RefreshPeriodFromUiAsync(
                MapPeriod(args.Period),
                refreshQuota: false);
        model.WeeklyQuotaHistoryRequested += (_, _) =>
            _ = RefreshWeeklyQuotaHistoryFromUiAsync();
        window.SettingsRequested += (_, _) => _ = OpenSettingsAsync();
        window.WidgetPositionChanged += (_, args) =>
        {
            settings.WindowLeft = args.Left;
            settings.WindowTop = args.Top;
            QueueSettingsSave();
        };
        window.Closing += MainWindowOnClosing;
        window.IsVisibleChanged += (_, _) =>
            trayIcon?.SetWindowVisible(window.IsVisible);

        model.PropertyChanged += ViewModelOnPropertyChanged;
    }

    private TrayIconService CreateTrayIcon()
    {
        return new TrayIconService(
            () => Dispatcher.Invoke(ToggleWindowVisibility),
            () => Dispatcher.Invoke(() => _ = RefreshDashboardAsync()),
            () => Dispatcher.Invoke(() => _ = OpenSettingsAsync()),
            enabled => Dispatcher.Invoke(() => _ = ChangeStartupAsync(enabled)),
            () => Dispatcher.Invoke(() => _ = ExitApplicationAsync()),
            ThemeService.ShouldUseLightTheme(settings.ThemeMode));
    }

    private async Task StartDashboardAsync()
    {
        if (dashboard is null || viewModel is null)
        {
            return;
        }

        try
        {
            await dashboard.StartAsync(settings.CodexHomePath, lifetime.Token);
            if (!string.IsNullOrWhiteSpace(dashboard.CurrentCodexHome))
            {
                settings.CodexHomePath = dashboard.CurrentCodexHome;
                await PersistSettingsAsync();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            viewModel.IsLive = false;
            viewModel.SetResetMessage("Loc.NoLocalCodexData");
            viewModel.SetRateLimitSummaryMessage("Loc.SelectCodexHome");
            trayIcon?.ShowBalloon(
                LocalizationService.Instance.Get("Loc.AppName"),
                LocalizationService.Instance.Format(
                    "Loc.ReadUsageFailedFormat",
                    exception.Message),
                System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private async Task RefreshDashboardAsync()
    {
        if (dashboard is null)
        {
            return;
        }

        try
        {
            if (dashboardStartTask is { IsCompleted: false } startTask)
            {
                await startTask;
            }

            await dashboard.RefreshNowAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshPeriodFromUiAsync(
        UsagePeriod period,
        bool refreshQuota)
    {
        if (dashboard is null)
        {
            return;
        }

        try
        {
            if (dashboardStartTask is { IsCompleted: false } startTask)
            {
                await startTask;
            }

            if (viewModel?.IsExpanded != true)
            {
                return;
            }

            await dashboard.RefreshPeriodAsync(
                period,
                refreshQuota,
                lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshWeeklyQuotaHistoryFromUiAsync()
    {
        if (dashboard is null)
        {
            return;
        }

        try
        {
            if (dashboardStartTask is { IsCompleted: false } startTask)
            {
                await startTask;
            }

            if (viewModel is not
                {
                    IsExpanded: true,
                    IsWeeklyQuotaOverlayOpen: true,
                })
            {
                return;
            }

            await dashboard.RefreshWeeklyQuotaHistoryAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (
            lifetime.IsCancellationRequested ||
            viewModel?.IsWeeklyQuotaOverlayOpen != true)
        {
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (mainWindow is null ||
            viewModel is null ||
            settingsService is null ||
            startupRegistration is null ||
            !await settingsDialogGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            CaptureSettingsFromUi();
            if (!mainWindow.IsVisible)
            {
                mainWindow.Show();
            }

            mainWindow.Expand();
            var dialog = new SettingsWindow(CreateSettingsSnapshot())
            {
                Owner = mainWindow,
                Topmost = mainWindow.Topmost,
            };
            var originalGlassTransparency =
                settings.GlassTransparencyPercent;
            var settingsAccepted = false;
            void PreviewGlassTransparency(int value) =>
                mainWindow.SetGlassTransparencyPercent(value);
            dialog.GlassTransparencyPreviewChanged +=
                PreviewGlassTransparency;
            try
            {
                settingsAccepted = dialog.ShowDialog() == true;
            }
            finally
            {
                dialog.GlassTransparencyPreviewChanged -=
                    PreviewGlassTransparency;
                if (!settingsAccepted)
                {
                    mainWindow.SetGlassTransparencyPercent(
                        originalGlassTransparency);
                }
            }

            if (!settingsAccepted || dialog.ResultSettings is null)
            {
                return;
            }

            var previousHome = settings.CodexHomePath;
            settings = dialog.ResultSettings.Normalize();
            ApplySettingsToUi(settings);

            try
            {
                startupRegistration.SetEnabled(
                    settings.StartWithWindows,
                    GetExecutablePath());
                trayIcon?.SetStartupEnabled(settings.StartWithWindows);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or System.Security.SecurityException)
            {
                settings.StartWithWindows = SafeReadStartupState();
                trayIcon?.SetStartupEnabled(settings.StartWithWindows);
                System.Windows.MessageBox.Show(
                    mainWindow,
                    LocalizationService.Instance.Get(
                        "Loc.StartupPermission"),
                    LocalizationService.Instance.Get("Loc.AppName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            await PersistSettingsAsync();

            var homeChanged = !string.Equals(
                previousHome,
                settings.CodexHomePath,
                StringComparison.OrdinalIgnoreCase);
            if (homeChanged && dashboard is not null)
            {
                await dashboard.ChangeCodexHomeAsync(
                    settings.CodexHomePath,
                    lifetime.Token);
                if (!string.IsNullOrWhiteSpace(dashboard.CurrentCodexHome))
                {
                    settings.CodexHomePath = dashboard.CurrentCodexHome;
                    await PersistSettingsAsync();
                }
            }
            if (dialog.RebuildIndexRequested && dashboard is not null)
            {
                await dashboard.RebuildIndexAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                mainWindow,
                LocalizationService.Instance.Format(
                    "Loc.SettingsApplyFailedFormat",
                    exception.Message),
                LocalizationService.Instance.Get("Loc.AppName"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            settingsDialogGate.Release();
            mainWindow?.RecheckAutoCollapse();
        }
    }

    private async Task ChangeStartupAsync(bool enabled)
    {
        if (startupRegistration is null)
        {
            return;
        }

        try
        {
            startupRegistration.SetEnabled(
                enabled,
                GetExecutablePath());
            settings.StartWithWindows = enabled;
            trayIcon?.SetStartupEnabled(enabled);
            await PersistSettingsAsync();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            settings.StartWithWindows = SafeReadStartupState();
            trayIcon?.SetStartupEnabled(settings.StartWithWindows);
            trayIcon?.ShowBalloon(
                LocalizationService.Instance.Get("Loc.AppName"),
                LocalizationService.Instance.Get("Loc.StartupPermission"),
                System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private async Task ExitApplicationAsync()
    {
        if (exitRequested)
        {
            return;
        }

        exitRequested = true;
        CaptureSettingsFromUi();
        settingsSaveTimer?.Stop();
        lifetime.Cancel();

        try
        {
            if (settingsService is not null)
            {
                await settingsService.SaveAsync(CreateSettingsSnapshot());
            }
        }
        catch
        {
            // Exiting must not be prevented by a transient settings write failure.
        }

        if (dashboard is not null)
        {
            await dashboard.DisposeAsync();
            dashboard = null;
        }

        trayIcon?.Dispose();
        trayIcon = null;
        if (mainWindow is not null)
        {
            mainWindow.Close();
            mainWindow = null;
        }

        singleInstance?.Dispose();
        singleInstance = null;
        cleanupComplete = true;
        Shutdown();
    }

    private void MainWindowOnClosing(object? sender, CancelEventArgs e)
    {
        if (exitRequested || mainWindow is null)
        {
            return;
        }

        e.Cancel = true;
        dashboard?.CancelPanelRefresh();
        mainWindow.Collapse(force: true);
        mainWindow.Hide();
        trayIcon?.SetWindowVisible(false);
    }

    private void ToggleWindowVisibility()
    {
        if (mainWindow is null)
        {
            return;
        }

        if (mainWindow.IsVisible)
        {
            dashboard?.CancelPanelRefresh();
            mainWindow.Collapse(force: true);
            mainWindow.Hide();
            return;
        }

        mainWindow.Show();
        mainWindow.Activate();
        if (viewModel?.IsPinned == true)
        {
            mainWindow.Expand();
        }
        else
        {
            mainWindow.Collapse(force: true);
        }
    }

    private void ViewModelOnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.RemainingPercent)
            or nameof(MainViewModel.RemainingDisplay)
            or nameof(MainViewModel.IsLive))
        {
            trayIcon?.SetUsagePercent(viewModel.RemainingPercent);
            UpdateTrayTooltip();
        }

        if (e.PropertyName is nameof(MainViewModel.IsPinned)
            or nameof(MainViewModel.SelectedPeriod))
        {
            CaptureSettingsFromUi();
            QueueSettingsSave();
        }

        if (e.PropertyName == nameof(MainViewModel.IsExpanded) &&
            !viewModel.IsExpanded)
        {
            dashboard?.CancelPanelRefresh();
        }

        if (e.PropertyName ==
                nameof(MainViewModel.IsWeeklyQuotaOverlayOpen) &&
            !viewModel.IsWeeklyQuotaOverlayOpen)
        {
            dashboard?.CancelPanelRefresh();
        }
    }

    private void ApplySettingsToUi(AppSettings source)
    {
        if (viewModel is null || mainWindow is null)
        {
            return;
        }

        ApplyLanguage(source.LanguageMode);
        viewModel.IsAlwaysOnTop = source.AlwaysOnTop;
        viewModel.AutoCollapseEnabled = source.AutoCollapse;
        viewModel.AutoCollapseDelayMilliseconds = source.AutoCollapseDelayMs;
        viewModel.IsPinned = source.IsPinned;
        viewModel.SelectedPeriod = viewModel.GetPeriod(ParsePeriod(source.SelectedPeriod));
        mainWindow.ApplyAppearance(
            ThemeService.ShouldUseLightTheme(source.ThemeMode));
        trayIcon?.ApplyTheme(
            ThemeService.ShouldUseLightTheme(source.ThemeMode));
        mainWindow.SetGlassTransparencyPercent(
            source.GlassTransparencyPercent);
        mainWindow.SetCollapsedMode(
            CollapsedWidgetModePolicy.Parse(source.CollapsedMode));
        mainWindow.SetAutoCollapse(source.AutoCollapse, source.AutoCollapseDelayMs);
        mainWindow.SetPinned(source.IsPinned);
    }

    private void ApplyLanguage(string languageMode)
    {
        LocalizationService.Instance.Apply(languageMode);
        mainWindow?.ApplyLocalization(LocalizationService.Instance.Culture);
        dashboard?.ReapplyLocalization();
        viewModel?.RefreshLocalization();
        trayIcon?.ApplyLocalization();
        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        if (viewModel is null || trayIcon is null)
        {
            return;
        }

        trayIcon.SetTooltip(
            viewModel.IsLive
                ? LocalizationService.Instance.Format(
                    "Loc.TrayRemainingFormat",
                    viewModel.RemainingDisplay)
                : LocalizationService.Instance.Get("Loc.TrayLoading"));
    }

    private void CaptureSettingsFromUi()
    {
        if (viewModel is null)
        {
            return;
        }

        settings.AlwaysOnTop = viewModel.IsAlwaysOnTop;
        settings.AutoCollapse = viewModel.AutoCollapseEnabled;
        settings.AutoCollapseDelayMs = viewModel.AutoCollapseDelayMilliseconds;
        settings.IsPinned = viewModel.IsPinned;
        settings.SelectedPeriod = FormatPeriod(viewModel.SelectedPeriod?.Kind);
        if (mainWindow is not null)
        {
            settings.CollapsedMode =
                CollapsedWidgetModePolicy.ToSettingValue(
                    mainWindow.CollapsedMode);
            settings.GlassTransparencyPercent =
                mainWindow.GlassTransparencyPercent;
        }

        if (mainWindow is { IsLoaded: true })
        {
            var position = mainWindow.GetPersistedPosition();
            settings.WindowLeft = position.Left;
            settings.WindowTop = position.Top;
        }
    }

    private AppSettings CreateSettingsSnapshot() => new()
    {
        CodexHomePath = settings.CodexHomePath,
        AlwaysOnTop = settings.AlwaysOnTop,
        AutoCollapse = settings.AutoCollapse,
        AutoCollapseDelayMs = settings.AutoCollapseDelayMs,
        StartWithWindows = settings.StartWithWindows,
        ThemeMode = settings.ThemeMode,
        LanguageMode = settings.LanguageMode,
        CollapsedMode = settings.CollapsedMode,
        GlassTransparencyPercent = settings.GlassTransparencyPercent,
        WindowLeft = settings.WindowLeft,
        WindowTop = settings.WindowTop,
        IsPinned = settings.IsPinned,
        SelectedPeriod = settings.SelectedPeriod,
    };

    private void QueueSettingsSave()
    {
        if (settingsSaveTimer is null || exitRequested)
        {
            return;
        }

        settingsSaveTimer.Stop();
        settingsSaveTimer.Start();
    }

    private async void SettingsSaveTimerOnTick(object? sender, EventArgs e)
    {
        settingsSaveTimer?.Stop();
        await PersistSettingsAsync();
    }

    private async Task PersistSettingsAsync()
    {
        if (settingsService is null || exitRequested)
        {
            return;
        }

        try
        {
            await settingsService.SaveAsync(
                CreateSettingsSnapshot(),
                lifetime.Token);
            settingsWriteWarningShown = false;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            QueueSettingsSave();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            if (!settingsWriteWarningShown)
            {
                settingsWriteWarningShown = true;
                trayIcon?.ShowBalloon(
                    LocalizationService.Instance.Get("Loc.AppName"),
                    LocalizationService.Instance.Get(
                        "Loc.SettingsSavePermission"),
                    System.Windows.Forms.ToolTipIcon.Warning);
            }
        }
    }

    private bool SafeReadStartupState()
    {
        try
        {
            return startupRegistration?.IsEnabled() == true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private void SystemEventsOnUserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs e)
    {
        if (exitRequested ||
            mainWindow is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                var useLightTheme = ThemeService.ShouldUseLightTheme(
                    settings.ThemeMode);
                mainWindow?.ApplyAppearance(useLightTheme);
                trayIcon?.ApplyTheme(useLightTheme);
            },
            DispatcherPriority.Background);
    }

    private void SystemEventsOnPowerModeChanged(
        object sender,
        PowerModeChangedEventArgs e)
    {
        if (exitRequested ||
            e.Mode != PowerModes.Resume ||
            dashboard is null)
        {
            return;
        }

        _ = dashboard.RecoverAfterResumeAsync(lifetime.Token);
    }

    private static void ApplyWindowPosition(
        MainWindow window,
        AppSettings source)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var workArea = SystemParameters.WorkArea;
        var collapsedWidth = window.CurrentCollapsedWidth;
        var left = source.WindowLeft;
        var top = source.WindowTop;

        if (left is { } savedLeft &&
            top is { } savedTop &&
            double.IsFinite(savedLeft) &&
            double.IsFinite(savedTop) &&
            savedLeft + collapsedWidth > SystemParameters.VirtualScreenLeft &&
            savedLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            savedTop + CodexUsageWidget.MainWindow.CollapsedHeight > SystemParameters.VirtualScreenTop &&
            savedTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            window.Left = savedLeft;
            window.Top = savedTop;
            return;
        }

        window.Left = Math.Max(
            workArea.Left,
            workArea.Right - collapsedWidth - 28);
        window.Top = workArea.Top + 48;
    }

    private static string GetExecutablePath() =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "CodexUsageWidget.exe");

    private static UsagePeriodKind ParsePeriod(string value) =>
        value switch
        {
            "Last7Days" => UsagePeriodKind.LastSevenDays,
            "ThisMonth" => UsagePeriodKind.CurrentMonth,
            "AllTime" => UsagePeriodKind.AllTime,
            _ => UsagePeriodKind.Today,
        };

    private static string FormatPeriod(UsagePeriodKind? value) =>
        value switch
        {
            UsagePeriodKind.LastSevenDays => "Last7Days",
            UsagePeriodKind.CurrentMonth => "ThisMonth",
            UsagePeriodKind.AllTime => "AllTime",
            _ => "Today",
        };

    private static UsagePeriod MapPeriod(UsagePeriodKind period) =>
        period switch
        {
            UsagePeriodKind.Today => UsagePeriod.Today,
            UsagePeriodKind.LastSevenDays => UsagePeriod.Last7Days,
            UsagePeriodKind.CurrentMonth => UsagePeriod.Month,
            _ => UsagePeriod.All,
        };
}
