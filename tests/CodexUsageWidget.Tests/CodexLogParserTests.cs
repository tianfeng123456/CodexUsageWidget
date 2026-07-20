using System.Text;
using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class CodexLogParserTests
{
    private static readonly string RootId =
        "11111111-1111-1111-1111-111111111111";

    [Theory]
    [InlineData("session", "parent", "id", "session")]
    [InlineData(null, "parent", "id", "parent")]
    [InlineData(null, null, "id", "id")]
    public void SessionMetadata_UsesRequiredRootPriority(
        string? sessionId,
        string? parentId,
        string id,
        string expected)
    {
        var metadata = new SessionMetadata(id, sessionId, parentId);

        Assert.Equal(expected, metadata.RootTaskId);
    }

    [Fact]
    public async Task ParseFile_ConvertsCumulativeUsageToDeltas_AndHandlesReset()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath(
            $"rollout-2026-07-18T10-00-00-{RootId}.jsonl");
        var start = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(start, 100, 40, 5, 10, 4),
            TestLog.TokenCount(start.AddMinutes(1), 150, 70, 9, 20, 8),
            TestLog.TokenCount(start.AddMinutes(2), 150, 70, 9, 20, 8),
            TestLog.TokenCount(start.AddMinutes(3), 20, 10, 2, 5, 1));

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        Assert.Equal(3, result.Deltas.Count);
        Assert.Equal(new TokenUsage(100, 40, 5, 10, 4), result.Deltas[0].Usage);
        Assert.Equal(new TokenUsage(50, 30, 4, 10, 4), result.Deltas[1].Usage);
        Assert.Equal(new TokenUsage(20, 10, 2, 5, 1), result.Deltas[2].Usage);
        Assert.Equal(195, result.Deltas.Sum(static item => item.Usage.TotalTokens));
        Assert.Equal(RootId, result.Checkpoint.RootTaskId);
    }

    [Fact]
    public void TokenUsageDelta_HandlesIndependentCounterResets()
    {
        var previous = new TokenUsage(100, 80, 20, 50, 30);
        var current = new TokenUsage(120, 10, 25, 5, 2);

        var delta = TokenUsage.Delta(current, previous);

        Assert.Equal(new TokenUsage(20, 10, 5, 5, 2), delta);
    }

    [Fact]
    public async Task ParseFile_AcceptsLegacyDirectTokenCountShape()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath($"rollout-legacy-{RootId}.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId),
            """
            {"timestamp":"2026-07-18T01:00:00Z","type":"token_count","payload":{"total_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":9,"reasoning_output_tokens":3,"total_tokens":89}}}
            """);

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        var only = Assert.Single(result.Deltas);
        Assert.Equal(new TokenUsage(80, 20, 0, 9, 3), only.Usage);
    }

    [Fact]
    public async Task ParseFile_SkipsTokenCountWithoutTimestamp()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath($"rollout-missing-time-{RootId}.jsonl");
        var validTimestamp =
            new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            """
            {"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":999,"cached_input_tokens":500,"output_tokens":1,"reasoning_output_tokens":1,"total_tokens":1000}},"rate_limits":{"limit_id":"codex","primary":{"used_percent":99,"window_minutes":10080}}}}
            """,
            TestLog.TokenCount(validTimestamp, 20, 10, 0, 3, 1));

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        var only = Assert.Single(result.Deltas);
        Assert.Equal(validTimestamp, only.Timestamp);
        Assert.Equal(23, only.Usage.TotalTokens);
        Assert.Empty(result.RateLimits);
        Assert.Equal(1, result.MalformedLineCount);
    }

    [Fact]
    public async Task ParseFile_ChildDropsReplayPrefixAndKeepsPostBoundaryUsage()
    {
        using var temporary = new TemporaryDirectory();
        const string childId = "22222222-2222-2222-2222-222222222222";
        var path = temporary.GetPath(
            $"rollout-2026-07-18T10-00-00-{childId}.jsonl");
        var start = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(childId, RootId, RootId),
            TestLog.TokenCount(
                start,
                1_000,
                800,
                0,
                100,
                50,
                usedPercent: 10),
            TestLog.TokenCount(
                start,
                2_000,
                1_600,
                0,
                200,
                100,
                usedPercent: 20),
            TestLog.ReplayBoundary(start),
            TestLog.TokenCount(
                start.AddSeconds(3),
                2_100,
                1_680,
                7,
                220,
                110,
                usedPercent: 30));

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        var only = Assert.Single(result.Deltas);
        Assert.Equal(RootId, only.RootTaskId);
        Assert.Equal(new TokenUsage(100, 80, 7, 20, 10), only.Usage);
        var rateLimit = Assert.Single(result.RateLimits).Snapshot;
        Assert.Equal(70, rateLimit.RemainingPercent);
        Assert.True(result.Checkpoint.ReplayBoundarySeen);
        Assert.NotNull(result.Checkpoint.FirstReplayBoundaryOffset);
        Assert.True(result.Checkpoint.IsChildSession);
    }

    [Fact]
    public async Task ParseFile_ChildKeepsUsageAcrossLaterTriggerMarkers()
    {
        using var temporary = new TemporaryDirectory();
        const string childId = "22222222-2222-2222-2222-222222222222";
        var path = temporary.GetPath($"rollout-multi-trigger-{childId}.jsonl");
        var start = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(childId, RootId, RootId),
            TestLog.TokenCount(start, 1_000, 800, 0, 100, 50),
            TestLog.ReplayBoundary(start.AddSeconds(1)),
            TestLog.TokenCount(start.AddSeconds(2), 1_100, 880, 0, 120, 60),
            TestLog.ReplayBoundary(start.AddSeconds(3)),
            TestLog.TokenCount(start.AddSeconds(4), 1_250, 1_000, 0, 150, 75));

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        Assert.Equal(2, result.Deltas.Count);
        Assert.Equal(
            new TokenUsage(250, 200, 0, 50, 25),
            result.Deltas.Aggregate(
                TokenUsage.Zero,
                static (sum, delta) => sum + delta.Usage));
    }

    [Fact]
    public async Task ParseFile_ChildFindsLargeReplayMarkerWithLateType()
    {
        using var temporary = new TemporaryDirectory();
        const string childId = "22222222-2222-2222-2222-222222222222";
        var path = temporary.GetPath($"rollout-large-trigger-{childId}.jsonl");
        var start = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);
        var metadata = TestLog.SessionMeta(childId, RootId, RootId, start);
        var replayed = TestLog.TokenCount(start, 100, 60, 0, 10, 2);
        var boundary = TestLog.LargeReplayBoundaryWithLateType(
            80 * 1024,
            start.AddSeconds(1));
        var accepted = TestLog.TokenCount(
            start.AddSeconds(2),
            150,
            90,
            0,
            20,
            4);

        await TestLog.WriteLinesAsync(
            path,
            metadata,
            replayed,
            boundary,
            accepted);

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        var only = Assert.Single(result.Deltas);
        Assert.Equal(new TokenUsage(50, 30, 0, 10, 2), only.Usage);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(metadata) +
            Encoding.UTF8.GetByteCount(replayed) +
            2L,
            result.Checkpoint.FirstReplayBoundaryOffset);
    }

    [Fact]
    public async Task ParseFile_RootKeepsUsageOnBothSidesOfTriggerMarker()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath($"rollout-root-trigger-{RootId}.jsonl");
        var start = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(start, 100, 80, 0, 10, 5),
            TestLog.ReplayBoundary(start.AddSeconds(1)),
            TestLog.TokenCount(start.AddSeconds(2), 150, 120, 0, 20, 10));

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        Assert.Equal(2, result.Deltas.Count);
        Assert.Equal(170, result.Deltas.Sum(delta => delta.Usage.TotalTokens));
        Assert.False(result.Checkpoint.IsChildSession);
    }

    [Fact]
    public async Task ParseFile_OldChildWithoutSessionId_UsesParentAndKeepsUsage()
    {
        using var temporary = new TemporaryDirectory();
        const string childId = "22222222-2222-2222-2222-222222222222";
        var path = temporary.GetPath($"rollout-old-{childId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(childId, null, RootId),
            TestLog.TokenCount(timestamp, 90, 30, 0, 10, 2));

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        var only = Assert.Single(result.Deltas);
        Assert.Equal(RootId, only.RootTaskId);
        Assert.Equal(100, only.Usage.TotalTokens);
    }

    [Fact]
    public async Task ParseFile_UpdatesRateLimitWhenInfoIsNull()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath($"rollout-rate-{RootId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(
                timestamp,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 20));

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        Assert.Empty(result.Deltas);
        var rate = Assert.Single(result.RateLimits).Snapshot;
        Assert.Equal(80, rate.RemainingPercent);
        Assert.Equal(10_080, rate.Primary?.WindowMinutes);
    }

    [Fact]
    public async Task ParseFile_SkipsHugeIrrelevantBody_AndReadsSharedFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath($"rollout-large-{RootId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.IrrelevantHugeLine(),
            TestLog.TokenCount(timestamp, 12, 4, 0, 3, 1));

        await using var heldOpen = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        Assert.Single(result.Deltas);
        Assert.Equal(15, result.Deltas[0].Usage.TotalTokens);
    }

    [Fact]
    public async Task ParseLatestRateLimitFromTail_SkipsPartialBody_AndPrefersCodex()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath($"rollout-tail-{RootId}.jsonl");
        var timestamp = new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);
        await TestLog.WriteLinesAsync(
            path,
            TestLog.IrrelevantHugeLine(256 * 1024),
            TestLog.TokenCount(
                timestamp,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 20,
                limitId: "codex"),
            TestLog.TokenCount(
                timestamp.AddMinutes(1),
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 99,
                limitId: "another-product"));
        await File.AppendAllTextAsync(path, """{"type":"event_msg","payload":""");

        await using var heldOpen = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var rate = await new CodexLogParser().ParseLatestRateLimitFromTailAsync(
            path,
            maximumTailBytes: 8 * 1024);

        Assert.NotNull(rate);
        Assert.Equal("codex", rate.LimitId);
        Assert.Equal(timestamp, rate.Timestamp);
        Assert.Equal(80, rate.RemainingPercent);
    }

    [Fact]
    public async Task ParseSessionIndex_UsesLatestTitleWithoutReadingSessions()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("session_index.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            """{"id":"11111111-1111-1111-1111-111111111111","thread_name":"旧标题","updated_at":"2026-07-17T00:00:00Z"}""",
            """{"id":"11111111-1111-1111-1111-111111111111","thread_name":"新标题","updated_at":"2026-07-18T00:00:00Z"}""",
            """{"id":"22222222-2222-2222-2222-222222222222","thread_name":"另一个任务","updated_at":"2026-07-18T00:00:00Z"}""");

        var entries = await new CodexLogParser().ParseSessionIndexAsync(path);

        Assert.Equal(2, entries.Count);
        Assert.Equal(
            "新标题",
            entries.Single(entry => entry.RootTaskId == RootId).Title);
    }
}
