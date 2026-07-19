using System.Security.Cryptography;

namespace CodexUsageWidget.Core;

public static class SharedFileAccess
{
    public static FileStream OpenRead(string path, bool asynchronous = true) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            asynchronous
                ? FileOptions.Asynchronous | FileOptions.SequentialScan
                : FileOptions.SequentialScan);

    public static async Task<string> ComputeCheckpointHashAsync(
        string path,
        long offset,
        CancellationToken cancellationToken = default)
    {
        if (offset <= 0)
        {
            return string.Empty;
        }

        await using var stream = OpenRead(path);
        var count = (int)Math.Min(512, offset);
        stream.Seek(offset - count, SeekOrigin.Begin);
        var buffer = new byte[count];
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
}
