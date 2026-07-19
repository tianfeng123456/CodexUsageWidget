using System.Buffers;
using System.Security.Cryptography;

namespace CodexUsageWidget.Core;

/// <summary>
/// Incrementally reads rate-limit JSONL rows without touching the usage index.
/// New files are sampled from a bounded tail; known files read only bytes that
/// appeared after their previous observation.
/// </summary>
public sealed class RateLimitTailMonitor
{
    private const int MaximumBufferedLineBytes = 128 * 1024;
    private const int BoundaryHashBytes = 512;

    private readonly int _maximumTailBytesPerNewFile;
    private readonly Dictionary<string, FileState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);

    public RateLimitTailMonitor(int maximumTailBytesPerNewFile = 512 * 1024)
    {
        if (maximumTailBytesPerNewFile <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTailBytesPerNewFile));
        }

        _maximumTailBytesPerNewFile = maximumTailBytesPerNewFile;
    }

    public Task<RateLimitTailReadResult> ReadChangedFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ReadChangedFilesAsync([path], cancellationToken);
    }

    public async Task<RateLimitTailReadResult> ReadChangedFilesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalizedPaths = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await _readGate.WaitAsync(cancellationToken);
        try
        {
            long totalBytesRead = 0;
            var pathsRead = new List<string>();
            var resetPaths = new List<string>();

            foreach (var path in normalizedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var outcome = await ReadOneAsync(path, cancellationToken);
                    if (outcome.ScanAttempted)
                    {
                        pathsRead.Add(path);
                    }

                    if (outcome.WasReset)
                    {
                        resetPaths.Add(path);
                    }

                    totalBytesRead = SaturatingAdd(
                        totalBytesRead,
                        outcome.BytesRead);
                }
                catch (FileNotFoundException)
                {
                    ForgetFile(path);
                }
                catch (DirectoryNotFoundException)
                {
                    ForgetFile(path);
                }
                catch (UnauthorizedAccessException)
                {
                    // A concurrently protected file must not hide other updates.
                }
                catch (IOException)
                {
                    // A concurrently replaced or moved file will be retried by
                    // the next watcher event or explicit bounded calibration.
                }
            }

            return new RateLimitTailReadResult(
                GetLatestSnapshot(),
                totalBytesRead,
                pathsRead,
                resetPaths);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public bool MoveCheckpoint(string oldPath, string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        var oldKey = NormalizePath(oldPath);
        var newKey = NormalizePath(newPath);

        lock (_stateLock)
        {
            if (!_states.Remove(oldKey, out var state))
            {
                return false;
            }

            _states[newKey] = state;
            return true;
        }
    }

    public bool ForgetFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_stateLock)
        {
            return _states.Remove(NormalizePath(path));
        }
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            _states.Clear();
        }
    }

    public bool TryGetCheckpoint(
        string path,
        out RateLimitTailCheckpoint? checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_stateLock)
        {
            if (_states.TryGetValue(NormalizePath(path), out var state))
            {
                checkpoint = state.Checkpoint;
                return true;
            }
        }

        checkpoint = null;
        return false;
    }

    private async Task<ReadOneOutcome> ReadOneAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileState? previous;
        lock (_stateLock)
        {
            _states.TryGetValue(path, out previous);
        }

        await using var stream = SharedFileAccess.OpenRead(path);
        var length = stream.Length;
        var lastWriteUtcTicks = File.GetLastWriteTimeUtc(path).Ticks;
        var wasReset = false;

        if (previous is not null)
        {
            if (length < previous.Checkpoint.KnownLength)
            {
                wasReset = true;
            }
            else
            {
                var currentBoundaryHash = await ComputeBoundaryHashAsync(
                    stream,
                    previous.Checkpoint.KnownLength,
                    cancellationToken);
                wasReset = !string.Equals(
                    currentBoundaryHash,
                    previous.Checkpoint.BoundaryHash,
                    StringComparison.Ordinal);

                // A same-length write cannot be an append. Reset even if the
                // small boundary probe happens to be unchanged.
                if (!wasReset &&
                    length == previous.Checkpoint.KnownLength &&
                    lastWriteUtcTicks != previous.Checkpoint.LastWriteUtcTicks)
                {
                    wasReset = true;
                }
            }
        }

        var isInitial = previous is null;
        var activeState = isInitial || wasReset ? null : previous;
        var scanStart = activeState is null
            ? Math.Max(0, length - _maximumTailBytesPerNewFile)
            : activeState.Checkpoint.KnownLength;

        if (activeState is not null && scanStart == length)
        {
            return new ReadOneOutcome(0, false, false);
        }

        var skipFirstPartialLine = false;
        if (activeState is null && scanStart > 0)
        {
            stream.Seek(scanStart - 1, SeekOrigin.Begin);
            skipFirstPartialLine = stream.ReadByte() != (byte)'\n';
        }

        var scan = await ScanNewBytesAsync(
            stream,
            scanStart,
            length,
            activeState,
            skipFirstPartialLine,
            cancellationToken);
        var boundaryHash = await ComputeBoundaryHashAsync(
            stream,
            length,
            cancellationToken);
        var checkpoint = new RateLimitTailCheckpoint(
            scan.CommittedOffset,
            length,
            lastWriteUtcTicks,
            boundaryHash,
            scan.LatestSnapshot);
        var updated = new FileState(
            checkpoint,
            scan.PendingBytes,
            scan.PendingWasTruncated,
            scan.SkipFirstPartialLine);

        lock (_stateLock)
        {
            _states[path] = updated;
        }

        return new ReadOneOutcome(scan.BytesRead, true, wasReset);
    }

    private static async Task<ScanOutcome> ScanNewBytesAsync(
        FileStream stream,
        long scanStart,
        long scanLength,
        FileState? previous,
        bool skipFirstPartialLine,
        CancellationToken cancellationToken)
    {
        stream.Seek(scanStart, SeekOrigin.Begin);
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var line = new MemoryStream();
        if (previous is not null && previous.PendingBytes.Length > 0)
        {
            line.Write(previous.PendingBytes);
        }

        var pendingWasTruncated = previous?.PendingWasTruncated ?? false;
        var skipping = previous?.SkipFirstPartialLine ?? skipFirstPartialLine;
        var committedOffset = previous?.Checkpoint.Offset ?? scanStart;
        var latest = previous?.Checkpoint.LatestSnapshot;
        var absolutePosition = scanStart;
        long bytesRead = 0;

        try
        {
            while (absolutePosition < scanLength)
            {
                var wanted = (int)Math.Min(
                    rented.Length,
                    scanLength - absolutePosition);
                var read = await stream.ReadAsync(
                    rented.AsMemory(0, wanted),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
                var segmentStart = 0;
                for (var index = 0; index < read; index++)
                {
                    if (rented[index] != (byte)'\n')
                    {
                        continue;
                    }

                    var count = index - segmentStart;
                    if (!skipping && count > 0)
                    {
                        WriteCapped(
                            line,
                            rented,
                            segmentStart,
                            count,
                            ref pendingWasTruncated);
                    }

                    absolutePosition += count + 1;
                    if (!skipping && !pendingWasTruncated)
                    {
                        var bytes = TrimCarriageReturn(line.ToArray());
                        if (CodexLogParser.TryParseRateLimitLine(
                                bytes,
                                out var candidate) &&
                            candidate is not null &&
                            IsCodex(candidate) &&
                            IsPreferred(candidate, latest))
                        {
                            latest = candidate;
                        }
                    }

                    committedOffset = absolutePosition;
                    skipping = false;
                    line.SetLength(0);
                    pendingWasTruncated = false;
                    segmentStart = index + 1;
                }

                if (segmentStart < read)
                {
                    var remaining = read - segmentStart;
                    if (!skipping)
                    {
                        WriteCapped(
                            line,
                            rented,
                            segmentStart,
                            remaining,
                            ref pendingWasTruncated);
                    }

                    absolutePosition += remaining;
                }
            }

            return new ScanOutcome(
                committedOffset,
                line.ToArray(),
                pendingWasTruncated,
                skipping,
                latest,
                bytesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void WriteCapped(
        MemoryStream line,
        byte[] buffer,
        int offset,
        int count,
        ref bool wasTruncated)
    {
        var available = MaximumBufferedLineBytes - (int)line.Length;
        if (available <= 0)
        {
            wasTruncated = true;
            return;
        }

        var toWrite = Math.Min(available, count);
        line.Write(buffer, offset, toWrite);
        if (toWrite < count)
        {
            wasTruncated = true;
        }
    }

    private static byte[] TrimCarriageReturn(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes[^1] != (byte)'\r')
        {
            return bytes;
        }

        return bytes[..^1];
    }

    private static async Task<string> ComputeBoundaryHashAsync(
        FileStream stream,
        long boundary,
        CancellationToken cancellationToken)
    {
        if (boundary <= 0)
        {
            return string.Empty;
        }

        var count = (int)Math.Min(BoundaryHashBytes, boundary);
        var buffer = new byte[count];
        stream.Seek(boundary - count, SeekOrigin.Begin);
        var read = 0;
        while (read < count)
        {
            var current = await stream.ReadAsync(
                buffer.AsMemory(read, count - read),
                cancellationToken);
            if (current == 0)
            {
                break;
            }

            read += current;
        }

        return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
    }

    private RateLimitSnapshot? GetLatestSnapshot()
    {
        lock (_stateLock)
        {
            RateLimitSnapshot? latest = null;
            foreach (var candidate in _states.Values
                         .Select(static state => state.Checkpoint.LatestSnapshot)
                         .Where(static snapshot => snapshot is not null))
            {
                if (IsPreferred(candidate!, latest))
                {
                    latest = candidate;
                }
            }

            return latest;
        }
    }

    private static bool IsCodex(RateLimitSnapshot snapshot) =>
        string.Equals(
            snapshot.LimitId,
            "codex",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsPreferred(
        RateLimitSnapshot candidate,
        RateLimitSnapshot? current) =>
        current is null || candidate.Timestamp >= current.Timestamp;

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right
            ? long.MaxValue
            : left + right;

    private sealed record FileState(
        RateLimitTailCheckpoint Checkpoint,
        byte[] PendingBytes,
        bool PendingWasTruncated,
        bool SkipFirstPartialLine);

    private sealed record ReadOneOutcome(
        long BytesRead,
        bool ScanAttempted,
        bool WasReset);

    private sealed record ScanOutcome(
        long CommittedOffset,
        byte[] PendingBytes,
        bool PendingWasTruncated,
        bool SkipFirstPartialLine,
        RateLimitSnapshot? LatestSnapshot,
        long BytesRead);
}

/// <summary>
/// Describes one observed file. <see cref="Offset"/> is the end of its last
/// complete JSONL row, while <see cref="KnownLength"/> may also include an
/// unfinished row retained by the monitor.
/// </summary>
public sealed record RateLimitTailCheckpoint(
    long Offset,
    long KnownLength,
    long LastWriteUtcTicks,
    string BoundaryHash,
    RateLimitSnapshot? LatestSnapshot);

/// <summary>
/// Reports logical scan bytes and paths. The small boundary-integrity probe is
/// intentionally excluded from <see cref="BytesRead"/>.
/// </summary>
public sealed record RateLimitTailReadResult(
    RateLimitSnapshot? LatestSnapshot,
    long BytesRead,
    IReadOnlyList<string> PathsRead,
    IReadOnlyList<string> ResetPaths);
