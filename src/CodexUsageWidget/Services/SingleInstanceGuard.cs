namespace CodexUsageWidget.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex mutex;
    private bool disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        this.mutex = mutex;
    }

    public static bool TryAcquire(out SingleInstanceGuard? guard)
    {
        var mutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\CodexUsageWidget.SingleInstance",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The OS will release ownership when the process exits.
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
