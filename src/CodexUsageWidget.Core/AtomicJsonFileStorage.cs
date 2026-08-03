using System.Text.Json;

namespace CodexUsageWidget.Core;

public enum JsonFileRecoverySource
{
    None,
    Primary,
    Temporary,
    Backup,
}

public sealed record JsonFileReadResult<T>(
    T? Value,
    JsonFileRecoverySource Source,
    bool RequiresPrimaryRepair,
    Exception? PrimaryFailure)
    where T : class;

/// <summary>
/// Reads a small local JSON state file with crash-recovery candidates and
/// writes updates through an atomic same-directory replacement. The primary
/// file is never overwritten until the replacement has been fully written,
/// flushed, and validated as JSON.
/// </summary>
public static class AtomicJsonFileStorage
{
    public const int DefaultMaximumBytes = 1024 * 1024;

    public static string GetTemporaryPath(string primaryPath) =>
        primaryPath + ".tmp";

    public static string GetBackupPath(string primaryPath) =>
        primaryPath + ".bak";

    public static string GetInvalidPath(string primaryPath) =>
        primaryPath + ".invalid";

    public static async Task<JsonFileReadResult<T>> ReadAsync<T>(
        string primaryPath,
        Func<JsonElement, T?> deserialize,
        int maximumBytes = DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
        ArgumentNullException.ThrowIfNull(deserialize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        Exception? primaryFailure = null;
        var candidates = new[]
        {
            (Path: primaryPath, Source: JsonFileRecoverySource.Primary),
            (Path: GetTemporaryPath(primaryPath), Source: JsonFileRecoverySource.Temporary),
            (Path: GetBackupPath(primaryPath), Source: JsonFileRecoverySource.Backup),
        };

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var bytes = await ReadBoundedAsync(
                    candidate.Path,
                    maximumBytes,
                    cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(RemoveUtf8Bom(bytes));
                var value = deserialize(document.RootElement);
                if (value is null)
                {
                    throw new JsonException("JSON state deserialized to null.");
                }

                return new JsonFileReadResult<T>(
                    value,
                    candidate.Source,
                    candidate.Source != JsonFileRecoverySource.Primary,
                    primaryFailure);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is JsonException or NotSupportedException or
                    InvalidDataException or IOException or
                    UnauthorizedAccessException or
                    System.Security.SecurityException)
            {
                if (candidate.Source == JsonFileRecoverySource.Primary)
                {
                    primaryFailure = exception;
                }
            }
        }

        return new JsonFileReadResult<T>(
            null,
            JsonFileRecoverySource.None,
            primaryFailure is not null,
            primaryFailure);
    }

    public static async Task WriteAsync(
        string primaryPath,
        ReadOnlyMemory<byte> utf8Json,
        bool replaceInvalidPrimary,
        int maximumBytes = DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        if (utf8Json.Length == 0 || utf8Json.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"JSON state must contain between 1 and {maximumBytes} bytes.");
        }

        var normalizedJson = RemoveUtf8Bom(utf8Json);
        if (normalizedJson.Length == 0)
        {
            throw new InvalidDataException("JSON state cannot contain only a BOM.");
        }

        // Reject an invalid replacement before touching any existing state.
        using (JsonDocument.Parse(normalizedJson))
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(primaryPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = GetTemporaryPath(primaryPath);
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.WriteAsync(normalizedJson, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        // Do not start the non-cancellable rename sequence after cancellation.
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(primaryPath))
        {
            if (replaceInvalidPrimary)
            {
                File.Move(
                    primaryPath,
                    GetInvalidPath(primaryPath),
                    overwrite: true);
                File.Move(temporaryPath, primaryPath);
            }
            else
            {
                File.Replace(
                    temporaryPath,
                    primaryPath,
                    GetBackupPath(primaryPath),
                    ignoreMetadataErrors: true);
            }
        }
        else
        {
            File.Move(temporaryPath, primaryPath);
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream)
        {
            if (stream.Length > maximumBytes)
            {
                throw new InvalidDataException(
                    $"JSON state exceeds the {maximumBytes}-byte safety limit.");
            }

            using var buffer = new MemoryStream(
                checked((int)Math.Min(stream.Length, maximumBytes)));
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(
                    chunk.AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"JSON state exceeds the {maximumBytes}-byte safety limit.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static ReadOnlyMemory<byte> RemoveUtf8Bom(
        ReadOnlyMemory<byte> bytes)
    {
        var span = bytes.Span;
        return span.Length >= 3 &&
               span[0] == 0xEF &&
               span[1] == 0xBB &&
               span[2] == 0xBF
            ? bytes[3..]
            : bytes;
    }
}
