using System.Diagnostics.CodeAnalysis;

namespace CodexUsageWidget.Core;

/// <summary>
/// Owns a named mutex for one primary process and a named auto-reset event
/// that lets later processes request activation without starting a second UI.
/// The names are supplied by the host so the synchronization behavior can be
/// exercised independently from WPF and without colliding with production.
/// </summary>
public sealed class NamedSingleInstanceCoordinator : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activationEvent;
    private readonly object registrationGate = new();
    private RegisteredWaitHandle? activationRegistration;
    private int disposed;

    private NamedSingleInstanceCoordinator(
        Mutex mutex,
        EventWaitHandle activationEvent)
    {
        this.mutex = mutex;
        this.activationEvent = activationEvent;
    }

    public event EventHandler? ActivationRequested;

    public static bool TryAcquire(
        string mutexName,
        string activationEventName,
        [NotNullWhen(true)] out NamedSingleInstanceCoordinator? coordinator,
        out bool activationSignaled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationEventName);

        var activationEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: activationEventName,
            createdNew: out var activationEventCreatedNew);
        var mutex = new Mutex(
            initiallyOwned: true,
            name: mutexName,
            createdNew: out var createdNew);
        if (!createdNew)
        {
            activationSignaled = !activationEventCreatedNew &&
                activationEvent.Set();
            activationEvent.Dispose();
            mutex.Dispose();
            coordinator = null;
            return false;
        }

        activationSignaled = false;
        coordinator = new NamedSingleInstanceCoordinator(mutex, activationEvent);
        return true;
    }

    public void StartListening()
    {
        lock (registrationGate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref disposed) != 0,
                this);
            activationRegistration ??=
                ThreadPool.RegisterWaitForSingleObject(
                    activationEvent,
                    static (state, timedOut) =>
                    {
                        if (!timedOut &&
                            state is NamedSingleInstanceCoordinator instance)
                        {
                            instance.OnActivationRequested();
                        }
                    },
                    this,
                    Timeout.Infinite,
                    executeOnlyOnce: false);
        }
    }

    private void OnActivationRequested()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // A callback that passed the disposed check immediately before this
        // assignment must still see no subscriber to invoke.
        ActivationRequested = null;
        lock (registrationGate)
        {
            activationRegistration?.Unregister(null);
            activationRegistration = null;
            activationEvent.Dispose();
        }
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The process owns the lifetime of this lease. If ownership was
            // already abandoned, closing the handle is still sufficient.
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
