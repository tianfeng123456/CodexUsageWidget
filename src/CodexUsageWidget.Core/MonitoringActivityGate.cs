namespace CodexUsageWidget.Core;

/// <summary>
/// Coordinates application-managed monitoring dormancy without suspending
/// the process. Every active interval owns a distinct generation and
/// cancellation token so work from an older interval cannot publish after
/// monitoring resumes.
/// </summary>
public sealed class MonitoringActivityGate : IDisposable
{
    private readonly object sync = new();
    private readonly List<CancellationTokenSource> retiredCancellations = [];
    private CancellationTokenSource? activeCancellation = new();
    private long generation = 1;
    private bool isPaused;
    private bool isDisposed;

    public bool IsPaused
    {
        get
        {
            lock (sync)
            {
                return isPaused || isDisposed;
            }
        }
    }

    public bool TryCapture(out MonitoringActivityLease lease)
    {
        lock (sync)
        {
            if (isPaused || isDisposed || activeCancellation is null)
            {
                lease = default;
                return false;
            }

            lease = new MonitoringActivityLease(
                generation,
                activeCancellation.Token);
            return true;
        }
    }

    public bool IsCurrent(MonitoringActivityLease lease)
    {
        lock (sync)
        {
            return !isPaused &&
                   !isDisposed &&
                   activeCancellation is not null &&
                   lease.Generation == generation &&
                   !lease.CancellationToken.IsCancellationRequested;
        }
    }

    /// <summary>
    /// Enters dormancy and synchronously signals cancellation to every lease
    /// captured from the previous active interval.
    /// </summary>
    public bool Pause()
    {
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            if (isPaused || isDisposed)
            {
                return false;
            }

            isPaused = true;
            generation++;
            cancellation = activeCancellation;
            activeCancellation = null;
            if (cancellation is not null)
            {
                // A caller may have captured the token immediately before
                // Pause. Keep its source alive until gate disposal so creating
                // a linked token in that race cannot observe a disposed source.
                retiredCancellations.Add(cancellation);
            }
        }

        Cancel(cancellation);
        return true;
    }

    /// <summary>
    /// Starts a new active interval. Calling this while already active is a
    /// no-op and does not invalidate current work.
    /// </summary>
    public bool Resume()
    {
        lock (sync)
        {
            if (isDisposed || !isPaused)
            {
                return false;
            }

            generation++;
            activeCancellation = new CancellationTokenSource();
            isPaused = false;
            return true;
        }
    }

    /// <summary>
    /// Releases cancellation sources from completed inactive generations.
    /// Callers must first wait for every operation that captured an old lease.
    /// This keeps repeated display-off/display-on cycles memory-stable.
    /// </summary>
    public void ReleaseRetiredCancellations()
    {
        CancellationTokenSource[] cancellations;
        lock (sync)
        {
            cancellations = retiredCancellations.ToArray();
            retiredCancellations.Clear();
        }

        foreach (var cancellation in cancellations)
        {
            cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource[] cancellations;
        lock (sync)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            isPaused = true;
            generation++;
            if (activeCancellation is not null)
            {
                retiredCancellations.Add(activeCancellation);
            }

            activeCancellation = null;
            cancellations = retiredCancellations.ToArray();
            retiredCancellations.Clear();
        }

        foreach (var cancellation in cancellations)
        {
            CancelAndDispose(cancellation);
        }
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (AggregateException)
        {
            // Dormancy must still be entered if a third-party cancellation
            // callback fails. The owning operation observes its own failure.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void CancelAndDispose(
        CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            Cancel(cancellation);
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}

public readonly record struct MonitoringActivityLease(
    long Generation,
    CancellationToken CancellationToken);
