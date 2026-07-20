namespace CodexUsageWidget.Tests;

public sealed class DisplayPowerLifecycleContractTests
{
    private const string DisplayPowerMonitorPath =
        "src/CodexUsageWidget/Services/DisplayPowerMonitor.cs";
    private const string AppPath =
        "src/CodexUsageWidget/App.xaml.cs";
    private const string AppSettingsPath =
        "src/CodexUsageWidget/Services/AppSettings.cs";
    private const string SettingsWindowPath =
        "src/CodexUsageWidget/SettingsWindow.xaml.cs";
    private const string DashboardControllerPath =
        "src/CodexUsageWidget/Services/DashboardController.cs";

    [Fact]
    public void DisplayMonitor_RegistersForSessionDisplayPowerBroadcasts()
    {
        string source = ReadRepositoryFile(DisplayPowerMonitorPath);

        Assert.Matches(
            @"WmPowerBroadcast\s*=\s*0x0218",
            source);
        Assert.Matches(
            @"PbtPowerSettingChange\s*=\s*0x8013",
            source);
        Assert.Matches(
            @"DeviceNotifyWindowHandle\s*=\s*0",
            source);
        Assert.Contains(
            "2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "source?.AddHook(WindowProc);",
            source,
            StringComparison.Ordinal);
        Assert.Matches(
            @"notificationHandle\s*=\s*" +
            @"RegisterPowerSettingNotification\(\s*" +
            @"handle\s*,\s*ref\s+setting\s*,\s*" +
            @"DeviceNotifyWindowHandle\s*\)",
            source);
        Assert.Matches(
            @"message\s*!=\s*WmPowerBroadcast\s*\|\|\s*" +
            @"wParam\.ToInt64\(\)\s*!=\s*" +
            @"PbtPowerSettingChange",
            source);
        Assert.Contains(
            "setting.PowerSetting != SessionDisplayStatus",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayMonitor_DisposeUnregistersNotificationAndWindowHook()
    {
        string source = ReadRepositoryFile(DisplayPowerMonitorPath);

        Assert.Contains(
            "window.SourceInitialized -= WindowOnSourceInitialized;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "window.Closed -= WindowOnClosed;",
            source,
            StringComparison.Ordinal);
        Assert.Matches(
            @"UnregisterPowerSettingNotification\(\s*" +
            @"notificationHandle\s*\)",
            source);
        Assert.Contains(
            "notificationHandle = IntPtr.Zero;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "source?.RemoveHook(WindowProc);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private static extern bool " +
            "UnregisterPowerSettingNotification(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppLifecycle_SubscribeAndUnsubscribeSessionAndPowerEvents()
    {
        string source = ReadRepositoryFile(AppPath);

        Assert.Contains(
            "displayPowerMonitor.DisplayStateChanged +=",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SystemEvents.PowerModeChanged +=",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SystemEvents.SessionSwitch +=",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "displayPowerMonitor.DisplayStateChanged -=",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SystemEvents.PowerModeChanged -=",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SystemEvents.SessionSwitch -=",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "displayPowerMonitor.Dispose();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SessionSwitchReason.SessionLock or",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SessionSwitchReason.SessionUnlock",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "e.Mode is not (PowerModes.Suspend or PowerModes.Resume)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PauseSetting_DefaultsOn_AndRoundTripsThroughUiAndSnapshot()
    {
        string settings = ReadRepositoryFile(AppSettingsPath);
        string app = ReadRepositoryFile(AppPath);
        string settingsWindow = ReadRepositoryFile(SettingsWindowPath);

        Assert.Matches(
            @"public\s+bool\s+PauseMonitoringWhenDisplayOff\s*" +
            @"\{\s*get;\s*set;\s*\}\s*=\s*true",
            settings);
        Assert.Contains(
            "private AppSettings CreateSettingsSnapshot() => new()",
            app,
            StringComparison.Ordinal);
        Assert.Matches(
            @"PauseMonitoringWhenDisplayOff\s*=\s*" +
            @"settings\.PauseMonitoringWhenDisplayOff",
            app);
        Assert.Matches(
            @"PauseMonitoringWhenDisplayOffCheckBox\.IsChecked\s*=\s*" +
            @"settings\.PauseMonitoringWhenDisplayOff",
            settingsWindow);
        Assert.Matches(
            @"PauseMonitoringWhenDisplayOff\s*=\s*" +
            @"PauseMonitoringWhenDisplayOffCheckBox\.IsChecked\s*==\s*true",
            settingsWindow);
    }

    [Fact]
    public void DisplayStateSemantics_OnlyOffEntersDormancy_DimDoesNot()
    {
        string monitor = ReadRepositoryFile(DisplayPowerMonitorPath);
        string app = ReadRepositoryFile(AppPath);

        Assert.Matches(
            @"enum\s+SessionDisplayState\s*\{\s*" +
            @"Off\s*=\s*0\s*,\s*" +
            @"On\s*=\s*1\s*,\s*" +
            @"Dimmed\s*=\s*2\s*,?\s*\}",
            monitor);
        Assert.Contains(
            "displayOff = e.State == SessionDisplayState.Off;",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "e.State != SessionDisplayState.On",
            app,
            StringComparison.Ordinal);
        Assert.Matches(
            @"var\s+shouldPause\s*=\s*" +
            @"settings\.PauseMonitoringWhenDisplayOff\s*&&\s*" +
            @"\(displayOff\s*\|\|\s*sessionLocked\s*\|\|\s*" +
            @"systemSuspended\)",
            app);
    }

    [Fact]
    public void DormancyLifecycle_PausesAndResumesThroughSingleGate()
    {
        string source = ReadRepositoryFile(AppPath);

        Assert.Contains(
            "await monitoringLifecycleGate.WaitAsync(lifetime.Token);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await dashboard.PauseMonitoringAsync(lifetime.Token);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "else if (dashboard.IsMonitoringPaused)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await dashboard.ResumeMonitoringAsync(lifetime.Token);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "monitoringLifecycleGate.Release();",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardDormancy_WaitsForSourceTransition_AndRejectsLateProgress()
    {
        string source = ReadRepositoryFile(DashboardControllerPath);

        Assert.Contains(
            "await sourceGate.WaitAsync();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "sender is not UsageIndexService service",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "!monitoringActivity.TryCapture(out var activity)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "!monitoringActivity.IsCurrent(activity)",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(FindRepositoryFile(relativePath));

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string platformPath = relativePath.Replace(
            '/',
            Path.DirectorySeparatorChar);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, platformPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' from the test output directory.");
    }
}
