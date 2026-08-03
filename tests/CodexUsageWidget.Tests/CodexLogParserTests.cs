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
        var metadata = new SessionMetadata(
            id,
            sessionId,
            parentId,
            null,
            null);

        Assert.Equal(expected, metadata.RootTaskId);
    }

    [Fact]
    public async Task ParseFile_ConvertsCumulativeUsageToHighWaterDeltas()
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

        Assert.Equal(2, result.Deltas.Count);
        Assert.Equal(new TokenUsage(100, 40, 5, 10, 4), result.Deltas[0].Usage);
        Assert.Equal(new TokenUsage(50, 30, 4, 10, 4), result.Deltas[1].Usage);
        Assert.Equal(170, result.Deltas.Sum(static item => item.Usage.TotalTokens));
        Assert.Equal(
            new TokenUsage(150, 70, 9, 20, 8),
            result.Checkpoint.HighWaterCumulative);
        Assert.Equal(RootId, result.Checkpoint.RootTaskId);
    }

    [Fact]
    public async Task ParseFile_ReportsMonotonicCompletedByteProgress()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath(
            $"rollout-progress-{RootId}.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.IrrelevantHugeLine(512 * 1024),
            TestLog.TokenCount(
                new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero),
                100,
                40,
                5,
                10,
                4));
        var length = new FileInfo(path).Length;
        var positions = new List<long>();

        await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path),
            cancellationToken: CancellationToken.None,
            progressCallback: positions.Add);

        Assert.True(positions.Count >= 2);
        Assert.Equal(length, positions[^1]);
        Assert.All(positions, position => Assert.InRange(position, 1, length));
        Assert.True(positions.SequenceEqual(positions.OrderBy(static value => value)));
    }

    [Fact]
    public void TokenUsageHighWater_IgnoresIndependentCounterRegressions()
    {
        var previous = new TokenUsage(100, 80, 20, 50, 30);
        var current = new TokenUsage(120, 10, 25, 5, 2);

        var delta = TokenUsage.DeltaAboveHighWater(current, previous);
        var highWater = TokenUsage.MergeHighWater(previous, current);

        Assert.Equal(new TokenUsage(20, 0, 5, 0, 0), delta);
        Assert.Equal(new TokenUsage(120, 80, 25, 50, 30), highWater);
    }

    [Fact]
    public async Task ParseFile_SmallRollbackDoesNotCreateLargePhantomDelta()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath($"rollout-jitter-{RootId}.jsonl");
        var start = new DateTimeOffset(2026, 7, 28, 4, 15, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(start, 603_764_545, 500_000_000, 0, 1_000, 100),
            TestLog.TokenCount(
                start.AddSeconds(1),
                603_754_655,
                499_999_000,
                0,
                999,
                99),
            TestLog.TokenCount(
                start.AddSeconds(2),
                603_800_000,
                500_020_000,
                0,
                1_100,
                110));

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        Assert.Equal(2, result.Deltas.Count);
        Assert.Equal(
            new TokenUsage(603_800_000, 500_020_000, 0, 1_100, 110),
            result.Deltas.Aggregate(
                TokenUsage.Zero,
                static (sum, item) => sum + item.Usage));
        Assert.Equal(
            new TokenUsage(603_800_000, 500_020_000, 0, 1_100, 110),
            result.Checkpoint.HighWaterCumulative);
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
    public async Task ParseFile_SkipsTimestampsTooCloseToDateTimeBoundaries()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath($"rollout-extreme-time-{RootId}.jsonl");
        var validTimestamp =
            new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            TestLog.TokenCount(DateTimeOffset.MinValue.AddDays(1), 900, 0, 0, 1, 0),
            TestLog.TokenCount(DateTimeOffset.MaxValue.AddDays(-1), 950, 0, 0, 2, 0),
            TestLog.TokenCount(validTimestamp, 20, 10, 0, 3, 1));

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        var only = Assert.Single(result.Deltas);
        Assert.Equal(validTimestamp, only.Timestamp);
        Assert.Equal(23, only.Usage.TotalTokens);
        Assert.Equal(2, result.MalformedLineCount);
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
        Assert.True(result.Checkpoint.RequiresReplayTrim);
    }

    [Fact]
    public async Task ParseFile_RootLikeSubagentForkDropsCopiedHistory()
    {
        using var temporary = new TemporaryDirectory();
        const string forkId = "33333333-3333-3333-3333-333333333333";
        var parentPath = temporary.GetPath($"rollout-parent-{RootId}.jsonl");
        var path = temporary.GetPath($"rollout-root-fork-{forkId}.jsonl");
        var start = new DateTimeOffset(2026, 7, 28, 20, 25, 39, TimeSpan.Zero);

        await TestLog.WriteLinesAsync(
            parentPath,
            TestLog.SessionMeta(RootId, RootId, timestamp: start),
            TestLog.TokenCount(start.AddSeconds(1), 100, 80, 0, 10, 5),
            // Trigger markers can themselves be part of the copied history.
            TestLog.ReplayBoundary(start.AddSeconds(2)),
            TestLog.TokenCount(start.AddSeconds(3), 200, 160, 0, 20, 10),
            TestLog.TokenCount(start.AddSeconds(4), 300, 240, 0, 30, 15));
        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(
                forkId,
                forkId,
                timestamp: start,
                forkedFromId: RootId,
                threadSource: "subagent"),
            TestLog.TokenCount(start.AddSeconds(1), 100, 80, 0, 10, 5),
            TestLog.ReplayBoundary(start.AddSeconds(2)),
            TestLog.TokenCount(start.AddSeconds(3), 200, 160, 0, 20, 10),
            TestLog.TokenCount(start.AddSeconds(5), 250, 200, 0, 25, 12));

        var parser = new CodexLogParser();
        var metadata = await parser.ReadInitialSessionMetadataAsync(path);
        var replayCutoff = await parser.FindForkReplayPrefixEndOffsetAsync(
            path,
            parentPath);
        var result = await parser.ParseForkFileAsync(
            path,
            UsageRepository.GetSourceKey(path),
            replayCutoff);

        Assert.NotNull(metadata);
        Assert.True(metadata.HasForkedHistory);
        Assert.True(metadata.IsRootLikeSubagentFork);
        Assert.True(replayCutoff > 0);
        Assert.Equal(RootId, metadata.RootTaskId);
        Assert.True(result.Checkpoint.RequiresReplayTrim);
        Assert.Equal(RootId, result.Checkpoint.RootTaskId);
        var only = Assert.Single(result.Deltas);
        Assert.Equal(new TokenUsage(50, 40, 0, 5, 2), only.Usage);
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
        Assert.False(result.Checkpoint.RequiresReplayTrim);
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
    public async Task ParseFile_RejectsNonFiniteRateLimitPercentages()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath($"rollout-rate-nonfinite-{RootId}.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            """
            {"timestamp":"2026-07-18T01:00:00.0000000+00:00","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","primary":{"used_percent":"NaN","window_minutes":10080},"secondary":{"used_percent":"Infinity","window_minutes":300}}}}
            """);

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        Assert.Empty(result.RateLimits);
    }

    [Fact]
    public async Task ParseFile_AcceptsStringWindowAndIsoResetTime()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath($"rollout-rate-string-{RootId}.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            """
            {"timestamp":"2026-07-18T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","primary":{"used_percent":12.5,"window_minutes":"10080","resets_at":"2026-07-25T00:00:00Z"}}}}
            """);

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        var window = Assert.Single(result.RateLimits).Snapshot.Primary;
        Assert.NotNull(window);
        Assert.Equal(10_080, window.WindowMinutes);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero),
            window.ResetsAt);
    }

    [Fact]
    public async Task ParseFile_DropsOutOfRangeNumericResetTime()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath($"rollout-rate-reset-overflow-{RootId}.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.SessionMeta(RootId, RootId),
            """
            {"timestamp":"2026-07-18T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","primary":{"used_percent":12.5,"window_minutes":10080,"resets_at":9223372036854775807}}}}
            """);

        var result = await new CodexLogParser().ParseFileAsync(
            path,
            UsageRepository.GetSourceKey(path));

        Assert.Null(Assert.Single(result.RateLimits).Snapshot.Primary?.ResetsAt);
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

    [Fact]
    public async Task ParseSessionIndex_BoundsDamagedIdentifiersAndTitles()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("session_index.jsonl");
        var oversizedId = new string('i', 513);
        var oversizedTitle = new string('题', 2048);
        await TestLog.WriteLinesAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(
                new { id = oversizedId, thread_name = "ignored" }),
            System.Text.Json.JsonSerializer.Serialize(
                new { id = RootId, thread_name = oversizedTitle }));

        var entries = await new CodexLogParser().ParseSessionIndexAsync(path);

        var entry = Assert.Single(entries);
        Assert.Equal(RootId, entry.RootTaskId);
        Assert.Equal(1024, entry.Title.Length);
    }

    [Fact]
    public async Task ParseFile_DropsOversizedMetadataIdentifier()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("rollout-no-id.jsonl");
        var oversizedId = new string('i', 513);
        await TestLog.WriteLinesAsync(
            path,
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    type = "session_meta",
                    payload = new
                    {
                        id = oversizedId,
                        session_id = oversizedId,
                    },
                }),
            TestLog.TokenCount(
                new DateTimeOffset(2026, 7, 18, 1, 0, 0, TimeSpan.Zero),
                100,
                50,
                0,
                20,
                5));

        var sourceKey = UsageRepository.GetSourceKey(path);
        var result = await new CodexLogParser().ParseFileAsync(path, sourceKey);

        Assert.Null(result.Checkpoint.RootTaskId);
        Assert.Null(result.Checkpoint.OwnSessionId);
        Assert.Equal(sourceKey, Assert.Single(result.Deltas).RootTaskId);
    }
}
