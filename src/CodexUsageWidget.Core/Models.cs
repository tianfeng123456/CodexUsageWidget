namespace CodexUsageWidget.Core;

public enum UsagePeriod
{
    Today,
    Last7Days,
    Month,
    All
}

public readonly record struct TokenUsage(
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens)
{
    public static TokenUsage Zero { get; } = new(0, 0, 0, 0, 0);

    // Cached input is part of input and reasoning output is part of output.
    public long TotalTokens => SaturatingAdd(InputTokens, OutputTokens);

    public bool IsZero =>
        InputTokens == 0 &&
        CachedInputTokens == 0 &&
        CacheWriteInputTokens == 0 &&
        OutputTokens == 0 &&
        ReasoningOutputTokens == 0;

    public TokenUsage NonNegative() => new(
        Math.Max(0, InputTokens),
        Math.Max(0, CachedInputTokens),
        Math.Max(0, CacheWriteInputTokens),
        Math.Max(0, OutputTokens),
        Math.Max(0, ReasoningOutputTokens));

    public static TokenUsage operator +(TokenUsage left, TokenUsage right) => new(
        SaturatingAdd(left.InputTokens, right.InputTokens),
        SaturatingAdd(left.CachedInputTokens, right.CachedInputTokens),
        SaturatingAdd(left.CacheWriteInputTokens, right.CacheWriteInputTokens),
        SaturatingAdd(left.OutputTokens, right.OutputTokens),
        SaturatingAdd(left.ReasoningOutputTokens, right.ReasoningOutputTokens));

    /// <summary>
    /// Returns only the portion of <paramref name="current"/> that exceeds the
    /// highest cumulative values already observed for the same rollout file.
    /// Lower concurrent snapshots are treated as stale observations. Physical
    /// file replacement and truncation are handled by the repository, which
    /// starts a fresh checkpoint before parsing the replacement content.
    /// </summary>
    public static TokenUsage DeltaAboveHighWater(
        TokenUsage current,
        TokenUsage? highWater)
    {
        current = current.NonNegative();
        if (highWater is null)
        {
            return current;
        }

        var before = highWater.Value.NonNegative();
        return new TokenUsage(
            IncreaseAboveHighWater(current.InputTokens, before.InputTokens),
            IncreaseAboveHighWater(
                current.CachedInputTokens,
                before.CachedInputTokens),
            IncreaseAboveHighWater(
                current.CacheWriteInputTokens,
                before.CacheWriteInputTokens),
            IncreaseAboveHighWater(current.OutputTokens, before.OutputTokens),
            IncreaseAboveHighWater(
                current.ReasoningOutputTokens,
                before.ReasoningOutputTokens));
    }

    public static TokenUsage MergeHighWater(
        TokenUsage? highWater,
        TokenUsage current)
    {
        current = current.NonNegative();
        if (highWater is null)
        {
            return current;
        }

        var before = highWater.Value.NonNegative();
        return new TokenUsage(
            Math.Max(before.InputTokens, current.InputTokens),
            Math.Max(before.CachedInputTokens, current.CachedInputTokens),
            Math.Max(
                before.CacheWriteInputTokens,
                current.CacheWriteInputTokens),
            Math.Max(before.OutputTokens, current.OutputTokens),
            Math.Max(
                before.ReasoningOutputTokens,
                current.ReasoningOutputTokens));
    }

    private static long IncreaseAboveHighWater(long current, long highWater) =>
        current > highWater
            ? current - highWater
            : 0;

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        if (right < 0 && left < long.MinValue - right)
        {
            return long.MinValue;
        }

        return left + right;
    }
}

public sealed record RateLimitWindowSnapshot(
    double UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => double.IsFinite(UsedPercent)
        ? Math.Clamp(100d - UsedPercent, 0d, 100d)
        : 0d;
}

public sealed record RateLimitSnapshot(
    DateTimeOffset Timestamp,
    string? LimitId,
    string? LimitName,
    string? PlanType,
    RateLimitWindowSnapshot? Primary,
    RateLimitWindowSnapshot? Secondary)
{
    public RateLimitWindowSnapshot? MostConstrained =>
        new[] { Primary, Secondary }
            .Where(static window =>
                window is not null && double.IsFinite(window.UsedPercent))
            .MinBy(static window => window!.RemainingPercent);

    public double? RemainingPercent => MostConstrained?.RemainingPercent;
}

/// <summary>
/// Captures directly observed Codex weekly allowance activity for one local
/// calendar date.
/// </summary>
/// <param name="LocalDate">Calendar date in the application's configured time zone.</param>
/// <param name="ConsumedPercentagePoints">
/// Increase in the weekly used percentage attributed to this date. The value is
/// built from monotonic high-water marks inside each server reset window so
/// stale concurrent snapshots and reset drops are not counted as usage.
/// </param>
/// <param name="ChangeFromPreviousDayPercentagePoints">
/// Difference between this date's consumed percentage points and the
/// immediately preceding calendar date's consumed percentage points. Null when
/// either date has no observation.
/// </param>
/// <param name="LastObservedUsedPercent">
/// Last accepted cumulative weekly used percentage observed on this date, or
/// <see langword="null"/> when the date has no observation.
/// </param>
/// <param name="LastObservedAt">
/// Time of that last accepted observation, converted to the application's
/// configured time zone, or <see langword="null"/> when the date has none.
/// </param>
/// <param name="ObservationCount">
/// Number of globally de-duplicated observations accepted for this date.
/// </param>
/// <param name="IsPartial">
/// True when the requested interval covers only part of the date, the date has
/// no observation, or the first observed reset window has no earlier baseline.
/// </param>
public sealed record DailyWeeklyRateLimitUsage(
    DateOnly LocalDate,
    double? ConsumedPercentagePoints,
    double? ChangeFromPreviousDayPercentagePoints,
    double? LastObservedUsedPercent,
    DateTimeOffset? LastObservedAt,
    int ObservationCount,
    bool IsPartial);

public sealed record TokenUsageDelta(
    string SourceKey,
    long EventOffset,
    DateTimeOffset Timestamp,
    string RootTaskId,
    TokenUsage Usage);

public sealed record SessionMetadata(
    string? Id,
    string? SessionId,
    string? ParentThreadId,
    string? ForkedFromId,
    string? ThreadSource)
{
    public string? RootTaskId => IsRootLikeSubagentFork
        ? ForkedFromId
        : FirstNonEmpty(SessionId, ParentThreadId, Id);

    public bool HasForkedHistory =>
        !string.IsNullOrWhiteSpace(ForkedFromId) &&
        !string.Equals(ForkedFromId, Id, StringComparison.OrdinalIgnoreCase);

    public bool IsRootLikeSubagentFork =>
        HasForkedHistory &&
        string.Equals(ThreadSource, "subagent", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(ParentThreadId) &&
        (string.IsNullOrWhiteSpace(SessionId) ||
         string.Equals(SessionId, Id, StringComparison.OrdinalIgnoreCase));

    public bool IsChildSession =>
        !string.IsNullOrWhiteSpace(RootTaskId) &&
        !string.IsNullOrWhiteSpace(Id) &&
        !string.Equals(RootTaskId, Id, StringComparison.OrdinalIgnoreCase);

    public bool RequiresReplayTrim => IsChildSession || HasForkedHistory;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
}

public sealed record LogParseCheckpoint(
    long Offset,
    string? RootTaskId,
    string? OwnSessionId,
    bool RequiresReplayTrim,
    TokenUsage? HighWaterCumulative,
    bool ReplayBoundarySeen,
    long? FirstReplayBoundaryOffset = null)
{
    public static LogParseCheckpoint Empty { get; } =
        new(0, null, null, false, null, false, null);
}

public sealed record LogParseResult(
    LogParseCheckpoint Checkpoint,
    IReadOnlyList<TokenUsageDelta> Deltas,
    IReadOnlyList<RateLimitSnapshotAtOffset> RateLimits,
    int MalformedLineCount);

public sealed record RateLimitSnapshotAtOffset(long EventOffset, RateLimitSnapshot Snapshot);

public sealed record SessionTitleEntry(
    string RootTaskId,
    string Title,
    DateTimeOffset? UpdatedAt);

public sealed record TaskUsageSnapshot(
    string RootTaskId,
    string Title,
    TokenUsage Usage,
    double PercentOfPeriod,
    bool IsArchived);

public sealed record UsageSummary(
    TokenUsage Total,
    int TaskCount,
    TokenUsage TopTasksTotal,
    TokenUsage OtherTasksTotal,
    double TopTasksPercent);

public sealed record PeriodSnapshot(
    UsagePeriod Period,
    DateOnly? FromDate,
    DateOnly? ToDate,
    IReadOnlyList<TaskUsageSnapshot> TopTasks,
    UsageSummary Summary,
    RateLimitSnapshot? RateLimits,
    DateTimeOffset LastUpdated);

public sealed record DashboardSnapshot(
    PeriodSnapshot Period,
    RateLimitSnapshot? RateLimits,
    DateTimeOffset LastUpdated,
    bool IsIndexing,
    double IndexProgress);

public sealed record RefreshResult(
    int FilesScanned,
    int FilesChanged,
    long BytesProcessed,
    DateTimeOffset CompletedAt,
    bool CompletedSuccessfully);

public enum IndexProgressStage
{
    Idle,
    Preparing,
    Reading,
    Finalizing,
    Completed,
    Incomplete,
}

public sealed class IndexProgressChangedEventArgs : EventArgs
{
    public IndexProgressChangedEventArgs(
        long processedBytes,
        long totalBytes,
        string? currentFile,
        IndexProgressStage stage)
    {
        ProcessedBytes = processedBytes;
        TotalBytes = totalBytes;
        CurrentFile = currentFile;
        Stage = stage;
    }

    public long ProcessedBytes { get; }

    public long TotalBytes { get; }

    public string? CurrentFile { get; }

    public IndexProgressStage Stage { get; }

    public bool IsComplete => Stage == IndexProgressStage.Completed;

    public bool IsTerminal =>
        Stage is IndexProgressStage.Completed or IndexProgressStage.Incomplete;

    public double Progress =>
        TotalBytes <= 0
            ? (IsComplete ? 1d : 0d)
            : Math.Clamp((double)ProcessedBytes / TotalBytes, 0d, 1d);
}

public sealed record UsageIndexOptions(
    string? CodexHome = null,
    string? DatabasePath = null,
    TimeZoneInfo? TimeZone = null,
    int TopTaskCount = 9);
