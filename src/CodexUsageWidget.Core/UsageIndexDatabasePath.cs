using System.Security.Cryptography;
using System.Text;

namespace CodexUsageWidget.Core;

public static class UsageIndexDatabasePath
{
    public static string ForHome(
        string appDataDirectory,
        string codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);

        var normalizedHome = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(codexHome.Trim())));
        var caseInsensitiveIdentity = normalizedHome.ToUpperInvariant();
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(caseInsensitiveIdentity)));

        return Path.Combine(
            Path.GetFullPath(appDataDirectory),
            $"usage-index-{hash}.db");
    }
}
