using System.Diagnostics.CodeAnalysis;
using CodexUsageWidget.Core;

namespace CodexUsageWidget.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName =
        @"Local\CodexUsageWidget.SingleInstance";
    private const string ActivationEventName =
        @"Local\CodexUsageWidget.ActivateExisting";

    private readonly NamedSingleInstanceCoordinator coordinator;

    private SingleInstanceGuard(
        NamedSingleInstanceCoordinator coordinator)
    {
        this.coordinator = coordinator;
    }

    public event EventHandler? ActivationRequested
    {
        add => coordinator.ActivationRequested += value;
        remove => coordinator.ActivationRequested -= value;
    }

    public static bool TryAcquire(
        [NotNullWhen(true)] out SingleInstanceGuard? guard,
        out bool activationSignaled)
    {
        if (!NamedSingleInstanceCoordinator.TryAcquire(
                MutexName,
                ActivationEventName,
                out var coordinator,
                out activationSignaled))
        {
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(coordinator);
        return true;
    }

    public void StartListening()
    {
        coordinator.StartListening();
    }

    public void Dispose()
    {
        coordinator.Dispose();
    }
}
