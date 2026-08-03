using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CodexUsageWidget.Core;

public sealed class CodexLogParser
{
    private const int MaximumIdentifierCharacters = 512;
    private const int MaximumTitleCharacters = 1024;
    private const int MaximumRateLimitLabelCharacters = 256;

    public Task<LogParseResult> ParseFileAsync(
        string path,
        string sourceKey,
        LogParseCheckpoint? checkpoint = null,
        Action<long>? progressCallback = null,
        CancellationToken cancellationToken = default) =>
        ParseFileCoreAsync(
            path,
            sourceKey,
            checkpoint,
            replayCutoffOverride: null,
            progressCallback,
            cancellationToken);

    public Task<LogParseResult> ParseForkFileAsync(
        string path,
        string sourceKey,
        long replayCutoffOffset,
        LogParseCheckpoint? checkpoint = null,
        Action<long>? progressCallback = null,
        CancellationToken cancellationToken = default) =>
        ParseFileCoreAsync(
            path,
            sourceKey,
            checkpoint,
            replayCutoffOffset,
            progressCallback,
            cancellationToken);

    private async Task<LogParseResult> ParseFileCoreAsync(
        string path,
        string sourceKey,
        LogParseCheckpoint? checkpoint,
        long? replayCutoffOverride,
        Action<long>? progressCallback,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        var state = checkpoint ?? LogParseCheckpoint.Empty;
        var rootTaskId = state.RootTaskId;
        var ownSessionId = state.OwnSessionId;
        var requiresReplayTrim = state.RequiresReplayTrim;
        var highWater = state.HighWaterCumulative;
        var replayBoundarySeen =
            state.ReplayBoundarySeen || replayCutoffOverride.HasValue;
        var firstReplayBoundaryOffset =
            replayCutoffOverride ?? state.FirstReplayBoundaryOffset;
        var nextOffset = state.Offset;
        var malformed = 0;
        var truncatedLineSeen = false;
        var deltas = new List<TokenUsageDelta>();
        var rateLimits = new List<RateLimitSnapshotAtOffset>();

        await using var stream = SharedFileAccess.OpenRead(path);
        if (state.Offset > stream.Length)
        {
            throw new InvalidOperationException(
                "日志文件已被截断；调用方应重置该文件的索引状态。");
        }

        await foreach (var line in ReadLinesAsync(
                           stream,
                           state.Offset,
                           64 * 1024,
                           cancellationToken,
                           progressCallback))
        {
            cancellationToken.ThrowIfCancellationRequested();
            truncatedLineSeen |= line.WasTruncated;

            if (line.Bytes.Length == 0)
            {
                nextOffset = line.EndOffset;
                continue;
            }

            try
            {
                if (!TryReadEnvelope(line, out var envelope))
                {
                    nextOffset = line.EndOffset;
                    continue;
                }

                if (string.Equals(
                        envelope.OuterType,
                        "inter_agent_communication_metadata",
                        StringComparison.Ordinal))
                {
                    firstReplayBoundaryOffset ??= line.StartOffset;
                    replayBoundarySeen = true;
                    nextOffset = line.EndOffset;
                    continue;
                }

                if (string.Equals(
                        envelope.OuterType,
                        "session_meta",
                        StringComparison.Ordinal))
                {
                    var metadata = new SessionMetadata(
                        envelope.Id,
                        envelope.SessionId,
                        envelope.ParentThreadId,
                        envelope.ForkedFromId,
                        envelope.ThreadSource);

                    if (string.IsNullOrWhiteSpace(rootTaskId))
                    {
                        rootTaskId = metadata.RootTaskId;
                        ownSessionId = metadata.Id;
                        requiresReplayTrim = metadata.RequiresReplayTrim;
                    }

                    nextOffset = line.EndOffset;
                    continue;
                }

                if (line.WasTruncated)
                {
                    malformed++;
                    nextOffset = line.EndOffset;
                    continue;
                }

                using var document = JsonDocument.Parse(line.Bytes);
                var root = document.RootElement;
                if (!TryGetTokenPayload(
                        root,
                        envelope.OuterType,
                        out var tokenPayload))
                {
                    nextOffset = line.EndOffset;
                    continue;
                }

                var timestamp = ReadTimestamp(root, tokenPayload);
                if (timestamp is null)
                {
                    malformed++;
                    if (line.IsTerminated)
                    {
                        nextOffset = line.EndOffset;
                    }

                    continue;
                }

                if (TryReadRateLimits(
                        tokenPayload,
                        timestamp.Value,
                        out var limitSnapshot))
                {
                    rateLimits.Add(new RateLimitSnapshotAtOffset(
                        line.StartOffset,
                        limitSnapshot));
                }

                if (!TryReadCumulativeUsage(tokenPayload, out var cumulative))
                {
                    nextOffset = line.EndOffset;
                    continue;
                }

                var delta = TokenUsage.DeltaAboveHighWater(
                    cumulative,
                    highWater);
                highWater = TokenUsage.MergeHighWater(highWater, cumulative);

                if (!delta.IsZero)
                {
                    var taskId = FirstNonEmpty(
                        rootTaskId,
                        ExtractSessionIdFromFileName(path),
                        sourceKey)!;

                    var usageDelta = new TokenUsageDelta(
                        sourceKey,
                        line.StartOffset,
                        timestamp.Value,
                        taskId,
                        delta);

                    deltas.Add(usageDelta);
                }

                nextOffset = line.EndOffset;
            }
            catch (JsonException)
            {
                malformed++;
                if (line.IsTerminated)
                {
                    // A complete malformed line will never become valid later.
                    nextOffset = line.EndOffset;
                }
            }
        }

        if (requiresReplayTrim &&
            firstReplayBoundaryOffset is null &&
            truncatedLineSeen)
        {
            // The normal parser deliberately caps irrelevant JSONL rows at
            // 64 KiB. A future marker may carry a larger metadata payload, or
            // place its top-level type after that payload. Retry only this
            // exceptional child-file case with a bounded larger line buffer.
            firstReplayBoundaryOffset =
                await FindFirstReplayBoundaryOffsetAsync(
                    path,
                    cancellationToken);
            replayBoundarySeen |= firstReplayBoundaryOffset is not null;
        }

        IReadOnlyList<TokenUsageDelta> acceptedDeltas = deltas;
        IReadOnlyList<RateLimitSnapshotAtOffset> acceptedRateLimits = rateLimits;
        if (requiresReplayTrim && firstReplayBoundaryOffset is long replayCutoff)
        {
            // Child and forked rollouts can start with copied history. For an
            // ordinary child this is the first trigger marker; for an explicit
            // fork the repository supplies the end of the matched parent
            // prefix. Later genuine usage remains above the cutoff.
            acceptedDeltas = deltas
                .Where(delta => delta.EventOffset > replayCutoff)
                .ToArray();
            acceptedRateLimits = rateLimits
                .Where(rateLimit => rateLimit.EventOffset > replayCutoff)
                .ToArray();
        }

        return new LogParseResult(
            new LogParseCheckpoint(
                nextOffset,
                rootTaskId,
                ownSessionId,
                requiresReplayTrim,
                highWater,
                replayBoundarySeen,
                firstReplayBoundaryOffset),
            acceptedDeltas,
            acceptedRateLimits,
            malformed);
    }

    /// <summary>
    /// Compares the cumulative token sequences in a fork and its source task
    /// and returns the fork-file offset of their longest identical prefix.
    /// A value of -1 means that no cumulative token row was copied.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The parser is injected as one replaceable service dependency; keeping the public parsing surface instance-based preserves that contract.")]
    public async Task<long> FindForkReplayPrefixEndOffsetAsync(
        string forkPath,
        string parentPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(forkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);

        await using var fork = ReadCumulativeSamplesAsync(
                forkPath,
                cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        await using var parent = ReadCumulativeSamplesAsync(
                parentPath,
                cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        long replayCutoff = -1;
        while (await fork.MoveNextAsync() && await parent.MoveNextAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fork.Current.Usage != parent.Current.Usage)
            {
                break;
            }

            replayCutoff = fork.Current.EventOffset;
        }

        return replayCutoff;
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The parser is injected as one replaceable service dependency; keeping the public parsing surface instance-based preserves that contract.")]
    public async Task<SessionMetadata?> ReadInitialSessionMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = SharedFileAccess.OpenRead(path);
        await foreach (var line in ReadLinesAsync(
                           stream,
                           0,
                           1024 * 1024,
                           cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Bytes.Length == 0)
            {
                continue;
            }

            try
            {
                if (TryReadEnvelope(line, out var envelope) &&
                    string.Equals(
                        envelope.OuterType,
                        "session_meta",
                        StringComparison.Ordinal))
                {
                    return new SessionMetadata(
                        envelope.Id,
                        envelope.SessionId,
                        envelope.ParentThreadId,
                        envelope.ForkedFromId,
                        envelope.ThreadSource);
                }
            }
            catch (JsonException)
            {
                return null;
            }

            // Rollout files place session_meta first. Do not turn a migration
            // check into a scan of the complete history when that invariant is
            // absent or the first row is malformed.
            return null;
        }

        return null;
    }

    private static async IAsyncEnumerable<CumulativeTokenSample>
        ReadCumulativeSamplesAsync(
            string path,
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = SharedFileAccess.OpenRead(path);
        await foreach (var line in ReadLinesAsync(
                           stream,
                           0,
                           64 * 1024,
                           cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Bytes.Length == 0 || line.WasTruncated)
            {
                continue;
            }

            CumulativeTokenSample? sample = null;
            try
            {
                if (!TryReadEnvelope(line, out var envelope))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line.Bytes);
                if (!TryGetTokenPayload(
                        document.RootElement,
                        envelope.OuterType,
                        out var tokenPayload) ||
                    !TryReadCumulativeUsage(tokenPayload, out var cumulative))
                {
                    continue;
                }

                sample = new CumulativeTokenSample(
                    line.StartOffset,
                    cumulative);
            }
            catch (JsonException)
            {
                // A malformed row cannot be part of a verified copied prefix.
            }

            if (sample is { } parsed)
            {
                yield return parsed;
            }
        }
    }

    /// <summary>
    /// Finds the first child trigger-turn marker as a top-level JSONL envelope.
    /// Text that merely mentions the marker inside a response body or source
    /// snippet is intentionally ignored.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The parser is injected as one replaceable service dependency; keeping the public parsing surface instance-based preserves that contract.")]
    public async Task<long?> FindFirstReplayBoundaryOffsetAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = SharedFileAccess.OpenRead(path);
        await foreach (var line in ReadLinesAsync(
                           stream,
                           0,
                           1024 * 1024,
                           cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Bytes.Length == 0)
            {
                continue;
            }

            try
            {
                if (TryReadEnvelope(line, out var envelope) &&
                    string.Equals(
                        envelope.OuterType,
                        "inter_agent_communication_metadata",
                        StringComparison.Ordinal))
                {
                    return line.StartOffset;
                }
            }
            catch (JsonException)
            {
                // Ignore malformed JSONL rows and continue to the first valid
                // top-level trigger marker.
            }
        }

        return null;
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The parser is injected as one replaceable service dependency; keeping the public parsing surface instance-based preserves that contract.")]
    public async Task<IReadOnlyList<SessionTitleEntry>> ParseSessionIndexAsync(
        string path,
        Action<long>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var latest = new Dictionary<string, SessionTitleEntry>(
            StringComparer.OrdinalIgnoreCase);

        await using var stream = SharedFileAccess.OpenRead(path);
        await foreach (var line in ReadLinesAsync(
                           stream,
                           0,
                           1024 * 1024,
                           cancellationToken,
                           progressCallback))
        {
            if (line.WasTruncated)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line.Bytes);
                var root = document.RootElement;
                var id = NormalizeIdentifier(FirstNonEmpty(
                    GetString(root, "id"),
                    GetString(root, "thread_id"),
                    GetString(root, "session_id")));
                var title = NormalizeDisplayText(FirstNonEmpty(
                    GetString(root, "thread_name"),
                    GetString(root, "title"),
                    GetString(root, "name")), MaximumTitleCharacters);

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var updatedAt = TryReadDateTimeOffset(root, "updated_at");
                var entry = new SessionTitleEntry(id, title, updatedAt);
                if (!latest.TryGetValue(id, out var existing) ||
                    Nullable.Compare(updatedAt, existing.UpdatedAt) >= 0)
                {
                    latest[id] = entry;
                }
            }
            catch (JsonException)
            {
                // Ignore malformed or partially-written index rows.
            }
        }

        return latest.Values.ToArray();
    }

    /// <summary>
    /// Reads only the tail of a rollout log and returns its newest Codex rate-limit
    /// snapshot. Unrelated JSONL rows are rejected by their envelope before their
    /// JSON body is parsed.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The parser is injected as one replaceable service dependency; keeping the public parsing surface instance-based preserves that contract.")]
    public async Task<RateLimitSnapshot?> ParseLatestRateLimitFromTailAsync(
        string path,
        int maximumTailBytes = 512 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTailBytes);

        await using var stream = SharedFileAccess.OpenRead(path);
        var length = stream.Length;
        var startOffset = Math.Max(0, length - maximumTailBytes);
        var skipFirstPartialLine = false;
        if (startOffset > 0)
        {
            stream.Seek(startOffset - 1, SeekOrigin.Begin);
            skipFirstPartialLine = stream.ReadByte() != (byte)'\n';
        }

        RateLimitSnapshot? latest = null;
        long latestOffset = -1;
        await foreach (var line in ReadLinesAsync(
                           stream,
                           startOffset,
                           64 * 1024,
                           cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (skipFirstPartialLine)
            {
                skipFirstPartialLine = false;
                continue;
            }

            if (line.Bytes.Length == 0 || line.WasTruncated)
            {
                continue;
            }

            try
            {
                if (!TryReadEnvelope(line, out var envelope))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line.Bytes);
                var root = document.RootElement;
                if (!TryGetTokenPayload(
                        root,
                        envelope.OuterType,
                        out var tokenPayload))
                {
                    continue;
                }

                var timestamp = ReadTimestamp(root, tokenPayload);
                if (timestamp is null ||
                    !TryReadRateLimits(
                        tokenPayload,
                        timestamp.Value,
                        out var snapshot))
                {
                    continue;
                }

                if (latest is null ||
                    IsPreferredRateLimit(snapshot, line.StartOffset, latest, latestOffset))
                {
                    latest = snapshot;
                    latestOffset = line.StartOffset;
                }
            }
            catch (JsonException)
            {
                // The writer may currently be appending the final JSONL row.
            }
        }

        return latest;
    }

    internal static bool TryParseRateLimitLine(
        byte[] utf8JsonLine,
        out RateLimitSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(utf8JsonLine);
        snapshot = null;
        if (utf8JsonLine.Length == 0)
        {
            return false;
        }

        try
        {
            var line = new ByteLine(
                0,
                utf8JsonLine.Length,
                utf8JsonLine,
                true,
                false);
            if (!TryReadEnvelope(line, out var envelope))
            {
                return false;
            }

            using var document = JsonDocument.Parse(utf8JsonLine);
            var root = document.RootElement;
            if (!TryGetTokenPayload(root, envelope.OuterType, out var tokenPayload))
            {
                return false;
            }

            var timestamp = ReadTimestamp(root, tokenPayload);
            if (timestamp is null ||
                !TryReadRateLimits(tokenPayload, timestamp.Value, out var parsed))
            {
                return false;
            }

            snapshot = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsPreferredRateLimit(
        RateLimitSnapshot candidate,
        long candidateOffset,
        RateLimitSnapshot current,
        long currentOffset)
    {
        var candidateIsCodex = string.Equals(
            candidate.LimitId,
            "codex",
            StringComparison.OrdinalIgnoreCase);
        var currentIsCodex = string.Equals(
            current.LimitId,
            "codex",
            StringComparison.OrdinalIgnoreCase);
        if (candidateIsCodex != currentIsCodex)
        {
            return candidateIsCodex;
        }

        var timestampComparison = candidate.Timestamp.CompareTo(current.Timestamp);
        return timestampComparison > 0 ||
               (timestampComparison == 0 && candidateOffset > currentOffset);
    }

    private static bool TryReadEnvelope(ByteLine line, out Envelope envelope)
    {
        string? outerType = null;
        string? innerType = null;
        string? id = null;
        string? sessionId = null;
        string? parentThreadId = null;
        string? forkedFromId = null;
        string? threadSource = null;

        var propertyReader = new Utf8JsonReader(
            line.Bytes,
            isFinalBlock: line.IsTerminated && !line.WasTruncated,
            state: default);
        while (propertyReader.Read())
        {
            if (propertyReader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var depth = propertyReader.CurrentDepth;
            var isType = propertyReader.ValueTextEquals("type"u8);
            var isId = propertyReader.ValueTextEquals("id"u8);
            var isSessionId = propertyReader.ValueTextEquals("session_id"u8);
            var isParentId = propertyReader.ValueTextEquals("parent_thread_id"u8);
            var isForkedFromId = propertyReader.ValueTextEquals("forked_from_id"u8);
            var isThreadSource = propertyReader.ValueTextEquals("thread_source"u8);
            if (!isType &&
                !isId &&
                !isSessionId &&
                !isParentId &&
                !isForkedFromId &&
                !isThreadSource)
            {
                continue;
            }

            if (!propertyReader.Read() ||
                propertyReader.TokenType != JsonTokenType.String)
            {
                continue;
            }

            if (isType && depth == 1)
            {
                outerType = propertyReader.GetString();
                if (!string.Equals(outerType, "event_msg", StringComparison.Ordinal) &&
                    !string.Equals(outerType, "session_meta", StringComparison.Ordinal) &&
                    !string.Equals(outerType, "token_count", StringComparison.Ordinal) &&
                    !string.Equals(
                        outerType,
                        "inter_agent_communication_metadata",
                        StringComparison.Ordinal))
                {
                    envelope = null!;
                    return false;
                }

                if (string.Equals(
                        outerType,
                        "event_msg",
                        StringComparison.Ordinal) &&
                    innerType is not null &&
                    !string.Equals(
                        innerType,
                        "token_count",
                        StringComparison.Ordinal))
                {
                    envelope = null!;
                    return false;
                }
            }
            else if (isType && depth == 2)
            {
                innerType = propertyReader.GetString();
                if (string.Equals(
                        outerType,
                        "event_msg",
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        innerType,
                        "token_count",
                        StringComparison.Ordinal))
                {
                    envelope = null!;
                    return false;
                }
            }
            else if (depth == 2 && isId)
            {
                id = NormalizeIdentifier(propertyReader.GetString());
            }
            else if (depth == 2 && isSessionId)
            {
                sessionId = NormalizeIdentifier(propertyReader.GetString());
            }
            else if (depth == 2 && isParentId)
            {
                parentThreadId = NormalizeIdentifier(propertyReader.GetString());
            }
            else if (depth == 2 && isForkedFromId)
            {
                forkedFromId = NormalizeIdentifier(propertyReader.GetString());
            }
            else if (depth == 2 && isThreadSource)
            {
                threadSource = NormalizeDisplayText(
                    propertyReader.GetString(),
                    MaximumRateLimitLabelCharacters);
            }
        }

        var relevant =
            string.Equals(outerType, "session_meta", StringComparison.Ordinal) ||
            string.Equals(
                outerType,
                "inter_agent_communication_metadata",
                StringComparison.Ordinal) ||
            string.Equals(outerType, "token_count", StringComparison.Ordinal) ||
            (string.Equals(outerType, "event_msg", StringComparison.Ordinal) &&
             string.Equals(innerType, "token_count", StringComparison.Ordinal));

        envelope = relevant
            ? new Envelope(
                outerType!,
                innerType,
                id,
                sessionId,
                parentThreadId,
                forkedFromId,
                threadSource)
            : null!;
        return relevant;
    }

    private static bool TryGetTokenPayload(
        JsonElement root,
        string? outerType,
        out JsonElement tokenPayload)
    {
        tokenPayload = default;
        if (string.Equals(outerType, "token_count", StringComparison.Ordinal))
        {
            tokenPayload = root.TryGetProperty("payload", out var directPayload)
                ? directPayload
                : root;
            return true;
        }

        if (!string.Equals(outerType, "event_msg", StringComparison.Ordinal) ||
            !root.TryGetProperty("payload", out var payload) ||
            !string.Equals(GetString(payload, "type"), "token_count", StringComparison.Ordinal))
        {
            return false;
        }

        tokenPayload = payload;
        return true;
    }

    private static bool TryReadCumulativeUsage(
        JsonElement payload,
        out TokenUsage usage)
    {
        JsonElement candidate;
        if (payload.TryGetProperty("info", out var info) &&
            info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("total_token_usage", out var infoUsage))
        {
            candidate = infoUsage;
        }
        else if (payload.TryGetProperty("total_token_usage", out var totalUsage))
        {
            candidate = totalUsage;
        }
        else if (payload.TryGetProperty("usage", out var directUsage))
        {
            candidate = directUsage;
        }
        else
        {
            usage = TokenUsage.Zero;
            return false;
        }

        if (candidate.ValueKind != JsonValueKind.Object)
        {
            usage = TokenUsage.Zero;
            return false;
        }

        var input = GetLong(candidate, "input_tokens", "input");
        var cached = GetLong(
            candidate,
            "cached_input_tokens",
            "cache_read_input_tokens",
            "cached_input");
        var cacheWrite = GetLong(
            candidate,
            "cache_write_input_tokens",
            "cache_creation_input_tokens");
        var output = GetLong(candidate, "output_tokens", "output");
        var reasoning = GetLong(
            candidate,
            "reasoning_output_tokens",
            "reasoning_tokens");

        usage = new TokenUsage(input, cached, cacheWrite, output, reasoning).NonNegative();
        return input != 0 ||
               cached != 0 ||
               cacheWrite != 0 ||
               output != 0 ||
               reasoning != 0 ||
               candidate.TryGetProperty("total_tokens", out _);
    }

    private static bool TryReadRateLimits(
        JsonElement payload,
        DateTimeOffset timestamp,
        out RateLimitSnapshot snapshot)
    {
        JsonElement limits;
        if (payload.TryGetProperty("rate_limits", out var directLimits))
        {
            limits = directLimits;
        }
        else if (payload.TryGetProperty("info", out var info) &&
                 info.ValueKind == JsonValueKind.Object &&
                 info.TryGetProperty("rate_limits", out var nestedLimits))
        {
            limits = nestedLimits;
        }
        else
        {
            snapshot = null!;
            return false;
        }

        if (limits.ValueKind != JsonValueKind.Object)
        {
            snapshot = null!;
            return false;
        }

        var primary = TryReadWindow(limits, "primary");
        var secondary = TryReadWindow(limits, "secondary");
        if (primary is null && secondary is null)
        {
            snapshot = null!;
            return false;
        }

        snapshot = new RateLimitSnapshot(
            timestamp,
            NormalizeDisplayText(
                GetString(limits, "limit_id"),
                MaximumRateLimitLabelCharacters),
            NormalizeDisplayText(
                GetString(limits, "limit_name"),
                MaximumRateLimitLabelCharacters),
            NormalizeDisplayText(
                GetString(limits, "plan_type"),
                MaximumRateLimitLabelCharacters),
            primary,
            secondary);
        return true;
    }

    private static RateLimitWindowSnapshot? TryReadWindow(
        JsonElement limits,
        string propertyName)
    {
        if (!limits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !TryGetDouble(window, "used_percent", out var usedPercent) ||
            !double.IsFinite(usedPercent))
        {
            return null;
        }

        int? windowMinutes = null;
        if (TryGetLong(window, "window_minutes", out var minutes))
        {
            windowMinutes = (int)Math.Clamp(minutes, 0, int.MaxValue);
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var resetElement))
        {
            resetsAt = ParseResetTime(resetElement);
        }

        return new RateLimitWindowSnapshot(
            Math.Clamp(usedPercent, 0d, 100d),
            windowMinutes,
            resetsAt);
    }

    private static DateTimeOffset? ParseResetTime(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt64(out var seconds))
        {
            try
            {
                var unixTimestamp = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return TimestampSafety.IsSupported(unixTimestamp)
                    ? unixTimestamp
                    : null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                element.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed) &&
            TimestampSafety.IsSupported(parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTimeOffset? ReadTimestamp(
        JsonElement root,
        JsonElement payload)
    {
        return TryReadDateTimeOffset(root, "timestamp") ??
               TryReadDateTimeOffset(payload, "timestamp");
    }

    private static DateTimeOffset? TryReadDateTimeOffset(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
                   value.GetString(),
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal |
                   System.Globalization.DateTimeStyles.AdjustToUniversal,
                   out var parsed) &&
               TimestampSafety.IsSupported(parsed)
            ? parsed
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetLong(element, propertyName, out var value))
            {
                return value;
            }
        }

        return 0;
    }

    private static bool TryGetLong(
        JsonElement element,
        string propertyName,
        out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetInt64(out value);
        }

        return property.ValueKind == JsonValueKind.String &&
               long.TryParse(
                   property.GetString(),
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryGetDouble(
        JsonElement element,
        string propertyName,
        out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetDouble(out value);
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(
                   property.GetString(),
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out value);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= MaximumIdentifierCharacters
            ? normalized
            : null;
    }

    private static string? NormalizeDisplayText(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length <= maximumCharacters)
        {
            return normalized;
        }

        var length = maximumCharacters;
        if (length > 0 &&
            char.IsHighSurrogate(normalized[length - 1]) &&
            length < normalized.Length &&
            char.IsLowSurrogate(normalized[length]))
        {
            length--;
        }

        return normalized[..length];
    }

    internal static string? ExtractSessionIdFromFileName(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var parts = fileName.Split('-');
        if (parts.Length < 5)
        {
            return null;
        }

        for (var index = 0; index <= parts.Length - 5; index++)
        {
            var candidate = string.Join("-", parts.Skip(index).Take(5));
            if (Guid.TryParse(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async IAsyncEnumerable<ByteLine> ReadLinesAsync(
        FileStream stream,
        long offset,
        int maximumBufferedLineBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action<long>? progressCallback = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumBufferedLineBytes);

        stream.Seek(offset, SeekOrigin.Begin);
        var lineStart = offset;
        var absolutePosition = offset;
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var line = new MemoryStream();
        var wasTruncated = false;

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(rented, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                var segmentStart = 0;
                for (var index = 0; index < read; index++)
                {
                    if (rented[index] != (byte)'\n')
                    {
                        continue;
                    }

                    var count = index - segmentStart;
                    if (count > 0)
                    {
                        WriteCapped(
                            line,
                            rented,
                            segmentStart,
                            count,
                            maximumBufferedLineBytes,
                            ref wasTruncated);
                    }

                    var bytes = TrimCarriageReturn(line.ToArray());
                    absolutePosition += count + 1;
                    yield return new ByteLine(
                        lineStart,
                        absolutePosition,
                        bytes,
                        true,
                        wasTruncated);

                    line.SetLength(0);
                    wasTruncated = false;
                    lineStart = absolutePosition;
                    segmentStart = index + 1;
                }

                if (segmentStart < read)
                {
                    var remaining = read - segmentStart;
                    WriteCapped(
                        line,
                        rented,
                        segmentStart,
                        remaining,
                        maximumBufferedLineBytes,
                        ref wasTruncated);
                    absolutePosition += remaining;
                }

                // Report the durable byte position only after every line in
                // this read block has been handed back to the parser. This
                // keeps progress tied to completed parsing work and limits the
                // callback rate to one update per 64 KiB read.
                progressCallback?.Invoke(absolutePosition);
            }

            if (line.Length > 0)
            {
                var bytes = TrimCarriageReturn(line.ToArray());
                yield return new ByteLine(
                    lineStart,
                    absolutePosition,
                    bytes,
                    false,
                    wasTruncated);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void WriteCapped(
        MemoryStream line,
        byte[] buffer,
        int offset,
        int count,
        int maximumBytes,
        ref bool wasTruncated)
    {
        var available = maximumBytes - (int)line.Length;
        if (available <= 0)
        {
            wasTruncated = true;
            return;
        }

        var toWrite = Math.Min(available, count);
        line.Write(buffer, offset, toWrite);
        if (toWrite < count)
        {
            wasTruncated = true;
        }
    }

    private static byte[] TrimCarriageReturn(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes[^1] != (byte)'\r')
        {
            return bytes;
        }

        return bytes[..^1];
    }

    private sealed record ByteLine(
        long StartOffset,
        long EndOffset,
        byte[] Bytes,
        bool IsTerminated,
        bool WasTruncated);

    private readonly record struct CumulativeTokenSample(
        long EventOffset,
        TokenUsage Usage);

    private sealed record Envelope(
        string OuterType,
        string? InnerType,
        string? Id,
        string? SessionId,
        string? ParentThreadId,
        string? ForkedFromId,
        string? ThreadSource);
}
