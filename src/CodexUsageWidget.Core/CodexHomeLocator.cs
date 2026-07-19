namespace CodexUsageWidget.Core;

public sealed record CodexHomePaths(
    string HomeDirectory,
    string SessionsDirectory,
    string ArchivedSessionsDirectory,
    string SessionIndexPath)
{
    public bool HasAnyDataSource =>
        Directory.Exists(SessionsDirectory) ||
        Directory.Exists(ArchivedSessionsDirectory) ||
        File.Exists(SessionIndexPath);

    public static CodexHomePaths FromHome(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        var fullPath = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(homeDirectory.Trim()));

        return new CodexHomePaths(
            fullPath,
            Path.Combine(fullPath, "sessions"),
            Path.Combine(fullPath, "archived_sessions"),
            Path.Combine(fullPath, "session_index.jsonl"));
    }
}

public static class CodexHomeLocator
{
    public static CodexHomePaths Detect(
        string? explicitPath = null,
        IEnumerable<string>? additionalCandidates = null)
    {
        var candidates = BuildCandidates(explicitPath, additionalCandidates);
        foreach (var candidate in candidates)
        {
            try
            {
                var paths = CodexHomePaths.FromHome(candidate);
                if (paths.HasAnyDataSource)
                {
                    return paths;
                }
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException)
            {
                // A candidate may be unavailable or malformed. Continue with the next one.
            }
        }

        throw new DirectoryNotFoundException(
            "未找到 Codex Home。请在设置中选择包含 sessions 或 archived_sessions 的目录。");
    }

    public static IReadOnlyList<string> BuildCandidates(
        string? explicitPath = null,
        IEnumerable<string>? additionalCandidates = null)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, explicitPath);
        AddCandidate(candidates, Environment.GetEnvironmentVariable("CODEX_HOME"));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddCandidate(candidates, Path.Combine(userProfile, ".codex"));
        }

        if (additionalCandidates is not null)
        {
            foreach (var candidate in additionalCandidates)
            {
                AddCandidate(candidates, candidate);
            }
        }

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                AddCandidate(candidates, Path.Combine(drive.Name, "Codex", "Home"));
            }
        }
        catch (IOException)
        {
            // Drive enumeration is only a fallback.
        }
        catch (UnauthorizedAccessException)
        {
            // Drive enumeration is only a fallback.
        }

        return candidates;
    }

    private static void AddCandidate(List<string> candidates, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(candidate);
        }
    }
}
