using System.Text;

namespace CodexUsageWidget.Core;

/// <summary>
/// Best-effort, error-only local diagnostics. Logging must never turn a
/// recoverable application error into another failure, so every filesystem
/// exception is contained and reported through the return value.
/// </summary>
public static class LocalDiagnosticLog
{
    private const long DefaultMaximumBytes = 1024 * 1024;
    private const string FileName = "diagnostics.log";
    private static readonly object Gate = new();
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    public static bool TryWrite(
        string directory,
        string operation,
        Exception exception,
        long maximumBytes = DefaultMaximumBytes)
    {
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(operation) ||
            exception is null ||
            maximumBytes <= 0)
        {
            return false;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, FileName);
                if (File.Exists(path) && new FileInfo(path).Length >= maximumBytes)
                {
                    File.Move(path, path + ".previous", overwrite: true);
                }

                var safeOperation = operation
                    .Replace('\r', ' ')
                    .Replace('\n', ' ');
                var entry =
                    $"[{DateTimeOffset.Now:O}] {safeOperation}{Environment.NewLine}" +
                    exception + Environment.NewLine + Environment.NewLine;
                File.AppendAllText(path, entry, Utf8WithoutBom);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
