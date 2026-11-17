using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CodexUsageWidget.Core;

/// <summary>
/// Supplements the legacy session_index with the desktop app's explicit thread
/// names. Opens only title metadata, read-only, inside the selected Codex Home.
/// Never selects first_user_message, preview, auth, or rollout content.
/// </summary>
public sealed class TaskTitleCatalog
{
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _legacyTitles = new(StringComparer.OrdinalIgnoreCase);

    public string Resolve(string id, string indexedTitle)
    {
        if (_names.TryGetValue(id, out var name)) return name;
        var shortId = id[..Math.Min(8, id.Length)];
        var missing = string.IsNullOrWhiteSpace(indexedTitle) ||
            indexedTitle == $"未命名任务 {shortId}" || indexedTitle == $"Untitled task {shortId}";
        return missing && _legacyTitles.TryGetValue(id, out var title) ? title : indexedTitle;
    }

    public static async Task<TaskTitleCatalog> ReadAsync(string home, CancellationToken cancellationToken = default)
    {
        var result = new TaskTitleCatalog();
        try
        {
            var files = Directory.EnumerateFiles(home, "state_*.sqlite", SearchOption.TopDirectoryOnly)
                .Select(path => (Path: path, Version: ParseVersion(path)))
                .Where(item => item.Version >= 0).OrderByDescending(item => item.Version);
            foreach (var file in files)
            {
                try
                {
                    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = file.Path, Mode = SqliteOpenMode.ReadOnly,
                        Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 2
                    }.ToString());
                    await connection.OpenAsync(cancellationToken);
                    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await using (var schema = connection.CreateCommand())
                    {
                        schema.CommandText = "PRAGMA table_info(threads);";
                        await using var reader = await schema.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
                    }
                    if (!columns.Contains("id") || (!columns.Contains("name") && !columns.Contains("title"))) continue;
                    // CASE guards avoid even returning long prompt-shaped fallback
                    // titles. Explicit name is the label shown by the Codex app.
                    var nameSql = columns.Contains("name")
                        ? "CASE WHEN length(name) BETWEEN 1 AND 1024 THEN name END" : "NULL";
                    var titleSql = columns.Contains("title")
                        ? "CASE WHEN length(title) BETWEEN 1 AND 256 AND instr(title, char(10)) = 0 AND instr(title, char(13)) = 0 THEN title END" : "NULL";
                    await using var query = connection.CreateCommand();
                    query.CommandText = $"SELECT id, {nameSql}, {titleSql} FROM threads;";
                    await using var rows = await query.ExecuteReaderAsync(cancellationToken);
                    while (await rows.ReadAsync(cancellationToken))
                    {
                        if (rows.IsDBNull(0)) continue;
                        var id = rows.GetString(0);
                        if (id.Length is 0 or > 512) continue;
                        if (!rows.IsDBNull(1) && Normalize(rows.GetString(1)) is { Length: > 0 } name)
                            result._names[id] = name;
                        if (!rows.IsDBNull(2) && Normalize(rows.GetString(2)) is { Length: > 0 } title)
                            result._legacyTitles[id] = title;
                    }
                    return result;
                }
                catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
                {
                    // Missing/locked/older schema metadata must not prevent quota
                    // and usage statistics from working. Retry on explicit refresh.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The selected Home can disappear between enumeration and refresh.
        }
        return result;
    }

    private static int ParseVersion(string path) => int.TryParse(
        Path.GetFileNameWithoutExtension(path).AsSpan("state_".Length),
        NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : -1;

    private static string Normalize(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
