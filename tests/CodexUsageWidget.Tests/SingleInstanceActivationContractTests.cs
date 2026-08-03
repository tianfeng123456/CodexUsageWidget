using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class SingleInstanceActivationContractTests
{
    [Fact]
    public void App_StartsActivationListenerInsideTheGuardedStartupBlock()
    {
        string source = ReadRepositoryFile("src/CodexUsageWidget/App.xaml.cs");
        int startupTry = source.IndexOf(
            "try\r\n        {\r\n            singleInstance.ActivationRequested +=",
            StringComparison.Ordinal);
        if (startupTry < 0)
        {
            startupTry = source.IndexOf(
                "try\n        {\n            singleInstance.ActivationRequested +=",
                StringComparison.Ordinal);
        }

        int listener = source.IndexOf(
            "singleInstance.StartListening();",
            StringComparison.Ordinal);
        int startupCatch = source.IndexOf(
            "catch (OperationCanceledException)",
            listener,
            StringComparison.Ordinal);

        Assert.True(startupTry >= 0);
        Assert.True(listener > startupTry);
        Assert.True(startupCatch > listener);
    }

    private const string GuardPath =
        "src/CodexUsageWidget/Services/SingleInstanceGuard.cs";
    private const string AppPath =
        "src/CodexUsageWidget/App.xaml.cs";

    [Fact]
    public void DuplicateInstance_SignalsPrimary_EvenBeforeListenerStarts()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\CodexUsageWidget.Tests.{suffix}.Mutex";
        var eventName = $@"Local\CodexUsageWidget.Tests.{suffix}.Activate";
        Assert.True(NamedSingleInstanceCoordinator.TryAcquire(
            mutexName,
            eventName,
            out var primary,
            out var primarySignal));
        Assert.False(primarySignal);
        Assert.NotNull(primary);
        using (var primaryLease = primary!)
        using (var activated = new ManualResetEventSlim())
        {
            Assert.False(NamedSingleInstanceCoordinator.TryAcquire(
                mutexName,
                eventName,
                out var duplicate,
                out var duplicateSignal));
            Assert.Null(duplicate);
            Assert.True(duplicateSignal);

            primaryLease.ActivationRequested += (_, _) => activated.Set();
            primaryLease.StartListening();

            Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void PrimaryLease_CanReceiveRepeatedRequests_AndBeReacquiredAfterDispose()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\CodexUsageWidget.Tests.{suffix}.Mutex";
        var eventName = $@"Local\CodexUsageWidget.Tests.{suffix}.Activate";
        Assert.True(NamedSingleInstanceCoordinator.TryAcquire(
            mutexName,
            eventName,
            out var primary,
            out _));
        Assert.NotNull(primary);
        var primaryLease = primary!;
        using var activated = new AutoResetEvent(false);
        primaryLease.ActivationRequested += (_, _) => activated.Set();
        primaryLease.StartListening();

        for (var request = 0; request < 3; request++)
        {
            Assert.False(NamedSingleInstanceCoordinator.TryAcquire(
                mutexName,
                eventName,
                out _,
                out var signaled));
            Assert.True(signaled);
            Assert.True(activated.WaitOne(TimeSpan.FromSeconds(5)));
        }

        primaryLease.Dispose();

        Assert.True(NamedSingleInstanceCoordinator.TryAcquire(
            mutexName,
            eventName,
            out var replacement,
            out var replacementSignal));
        Assert.False(replacementSignal);
        replacement!.Dispose();
    }

    [Fact]
    public void StartListening_IsIdempotentUnderConcurrentCalls()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\CodexUsageWidget.Tests.{suffix}.Mutex";
        var eventName = $@"Local\CodexUsageWidget.Tests.{suffix}.Activate";
        Assert.True(NamedSingleInstanceCoordinator.TryAcquire(
            mutexName,
            eventName,
            out var primary,
            out _));
        using var primaryLease = primary!;
        using var activated = new ManualResetEventSlim();
        var activationCount = 0;
        primaryLease.ActivationRequested += (_, _) =>
        {
            Interlocked.Increment(ref activationCount);
            activated.Set();
        };

        Parallel.For(0, 16, _ => primaryLease.StartListening());
        Assert.False(NamedSingleInstanceCoordinator.TryAcquire(
            mutexName,
            eventName,
            out _,
            out var signaled));
        Assert.True(signaled);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref activationCount));
    }

    [Fact]
    public void ProductionGuard_UsesTestedNamedCoordinatorAndStableNames()
    {
        string source = ReadRepositoryFile(GuardPath);

        Assert.Contains(
            @"Local\CodexUsageWidget.ActivateExisting",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "NamedSingleInstanceCoordinator.TryAcquire(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PrimaryInstance_ShowsAndActivatesItsExistingWindow()
    {
        string source = ReadRepositoryFile(AppPath);

        Assert.Contains(
            "singleInstance.ActivationRequested +=",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "singleInstance.StartListening();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Dispatcher.BeginInvoke(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void ShowExistingWindow()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "mainWindow.Show();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "activationTarget.Activate();",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateInstance_OnlyShowsLegacyNoticeWhenActivationUnavailable()
    {
        string source = ReadRepositoryFile(AppPath);

        Assert.Contains(
            "out var activationSignaled",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!activationSignaled)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Environment.Exit(0);",
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
