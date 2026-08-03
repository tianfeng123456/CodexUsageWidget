using System.Text.Json;

namespace CodexUsageWidget.Tests;

internal static class TestLog
{
    public static string SessionMeta(
        string id,
        string? sessionId = null,
        string? parentThreadId = null,
        DateTimeOffset? timestamp = null,
        string? forkedFromId = null,
        string? threadSource = null) =>
        Serialize(new Dictionary<string, object?>
        {
            ["timestamp"] = (timestamp ?? DateTimeOffset.UtcNow).ToString("O"),
            ["type"] = "session_meta",
            ["payload"] = new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["id"] = id,
                ["parent_thread_id"] = parentThreadId,
                ["forked_from_id"] = forkedFromId,
                ["thread_source"] = threadSource,
                // This intentionally proves that the parser ignores unrelated metadata.
                ["base_instructions"] = new string('x', 96 * 1024)
            }
        });

    public static string TokenCount(
        DateTimeOffset timestamp,
        long input,
        long cached,
        long cacheWrite,
        long output,
        long reasoning,
        bool includeInfo = true,
        double? usedPercent = null,
        string limitId = "codex")
    {
        object? info = includeInfo
            ? new Dictionary<string, object?>
            {
                ["total_token_usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = input,
                    ["cached_input_tokens"] = cached,
                    ["cache_write_input_tokens"] = cacheWrite,
                    ["output_tokens"] = output,
                    ["reasoning_output_tokens"] = reasoning,
                    ["total_tokens"] = input + output
                },
                // This deliberately differs from cumulative usage. It must be ignored.
                ["last_token_usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = 999_999,
                    ["output_tokens"] = 999_999
                }
            }
            : null;

        object? rateLimits = usedPercent is null
            ? null
            : new Dictionary<string, object?>
            {
                ["limit_id"] = limitId,
                ["limit_name"] = "Codex",
                ["plan_type"] = "pro",
                ["primary"] = new Dictionary<string, object?>
                {
                    ["used_percent"] = usedPercent.Value,
                    ["window_minutes"] = 10_080,
                    ["resets_at"] = timestamp.AddDays(2).ToUnixTimeSeconds()
                },
                ["secondary"] = null
            };

        return Serialize(new Dictionary<string, object?>
        {
            ["timestamp"] = timestamp.ToString("O"),
            ["type"] = "event_msg",
            ["payload"] = new Dictionary<string, object?>
            {
                ["type"] = "token_count",
                ["info"] = info,
                ["rate_limits"] = rateLimits
            }
        });
    }

    public static string WeeklyRateLimit(
        DateTimeOffset timestamp,
        double usedPercent,
        DateTimeOffset resetsAt,
        bool weeklyInSecondary = false,
        string limitId = "codex",
        int windowMinutes = 10_080)
    {
        var weekly = new Dictionary<string, object?>
        {
            ["used_percent"] = usedPercent,
            ["window_minutes"] = windowMinutes,
            ["resets_at"] = resetsAt.ToUnixTimeSeconds()
        };
        object? primary = weekly;
        object? secondary = null;
        if (weeklyInSecondary)
        {
            primary = new Dictionary<string, object?>
            {
                ["used_percent"] = 1d,
                ["window_minutes"] = 300,
                ["resets_at"] = timestamp.AddHours(5).ToUnixTimeSeconds()
            };
            secondary = weekly;
        }

        return Serialize(new Dictionary<string, object?>
        {
            ["timestamp"] = timestamp.ToString("O"),
            ["type"] = "event_msg",
            ["payload"] = new Dictionary<string, object?>
            {
                ["type"] = "token_count",
                ["info"] = null,
                ["rate_limits"] = new Dictionary<string, object?>
                {
                    ["limit_id"] = limitId,
                    ["limit_name"] = "Codex",
                    ["plan_type"] = "pro",
                    ["primary"] = primary,
                    ["secondary"] = secondary
                }
            }
        });
    }

    public static string ReplayBoundary(DateTimeOffset? timestamp = null) =>
        Serialize(new Dictionary<string, object?>
        {
            ["timestamp"] = (timestamp ?? DateTimeOffset.UtcNow).ToString("O"),
            ["type"] = "inter_agent_communication_metadata",
            ["payload"] = new Dictionary<string, object?>()
        });

    public static string LargeReplayBoundaryWithLateType(
        int payloadCharacters,
        DateTimeOffset? timestamp = null) =>
        Serialize(new Dictionary<string, object?>
        {
            ["timestamp"] = (timestamp ?? DateTimeOffset.UtcNow).ToString("O"),
            ["payload"] = new Dictionary<string, object?>
            {
                ["padding"] = new string('x', payloadCharacters)
            },
            ["type"] = "inter_agent_communication_metadata"
        });

    public static string IrrelevantHugeLine(int payloadBytes = 2 * 1024 * 1024) =>
        Serialize(new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["type"] = "response_item",
            ["payload"] = new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["content"] = new string('不', payloadBytes)
            }
        });

    public static async Task WriteLinesAsync(
        string path,
        params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            string.Join("\n", lines) + "\n");
    }

    public static async Task AppendLinesAsync(
        string path,
        params string[] lines) =>
        await File.AppendAllTextAsync(path, string.Join("\n", lines) + "\n");

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value);
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CodexUsageWidget.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string GetPath(params string[] parts)
    {
        var result = Path;
        foreach (var part in parts)
        {
            result = System.IO.Path.Combine(result, part);
        }

        return result;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
