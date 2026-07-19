using System.Text;
using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class RateLimitTailMonitorTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 18, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NewFile_ReadsBoundedTail_AndFindsLatestCodexQuota()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("rollout-bounded.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.IrrelevantHugeLine(64 * 1024),
            TestLog.TokenCount(
                Start,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 25));
        var monitor = new RateLimitTailMonitor(maximumTailBytesPerNewFile: 4096);

        var result = await monitor.ReadChangedFileAsync(path);

        Assert.InRange(result.BytesRead, 1, 4096);
        Assert.Equal(Path.GetFullPath(path), Assert.Single(result.PathsRead));
        Assert.Empty(result.ResetPaths);
        Assert.Equal(75, result.LatestSnapshot?.RemainingPercent);
    }

    [Fact]
    public async Task KnownFile_ReadsOnlyNewBytes_AndSkipsUnchangedFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("rollout-incremental.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.TokenCount(
                Start,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 10));
        var monitor = new RateLimitTailMonitor();
        await monitor.ReadChangedFileAsync(path);
        var appended = TestLog.TokenCount(
            Start.AddMinutes(1),
            0,
            0,
            0,
            0,
            0,
            includeInfo: false,
            usedPercent: 20) + "\n";
        await File.AppendAllTextAsync(path, appended);

        var changed = await monitor.ReadChangedFileAsync(path);
        var unchanged = await monitor.ReadChangedFileAsync(path);

        Assert.Equal(Encoding.UTF8.GetByteCount(appended), changed.BytesRead);
        Assert.Equal(80, changed.LatestSnapshot?.RemainingPercent);
        Assert.Equal(0, unchanged.BytesRead);
        Assert.Empty(unchanged.PathsRead);
    }

    [Fact]
    public async Task PartialLine_DoesNotAdvanceOffset_AndIsNotReread()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("rollout-partial.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.TokenCount(
                Start,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 10));
        var monitor = new RateLimitTailMonitor();
        await monitor.ReadChangedFileAsync(path);
        Assert.True(monitor.TryGetCheckpoint(path, out var before));

        var complete = TestLog.TokenCount(
            Start.AddMinutes(1),
            0,
            0,
            0,
            0,
            0,
            includeInfo: false,
            usedPercent: 30);
        var split = complete.Length / 2;
        var first = complete[..split];
        var second = complete[split..] + "\n";
        await File.AppendAllTextAsync(path, first);

        var partial = await monitor.ReadChangedFileAsync(path);
        Assert.True(monitor.TryGetCheckpoint(path, out var during));
        Assert.Equal(before!.Offset, during!.Offset);
        Assert.Equal(Encoding.UTF8.GetByteCount(first), partial.BytesRead);
        Assert.Equal(90, partial.LatestSnapshot?.RemainingPercent);

        await File.AppendAllTextAsync(path, second);
        var completed = await monitor.ReadChangedFileAsync(path);
        Assert.True(monitor.TryGetCheckpoint(path, out var after));

        Assert.Equal(Encoding.UTF8.GetByteCount(second), completed.BytesRead);
        Assert.Equal(new FileInfo(path).Length, after!.Offset);
        Assert.Equal(70, completed.LatestSnapshot?.RemainingPercent);
    }

    [Fact]
    public async Task IrrelevantAppend_KeepsPreviousQuota()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("rollout-irrelevant.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.TokenCount(
                Start,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 15));
        var monitor = new RateLimitTailMonitor();
        await monitor.ReadChangedFileAsync(path);
        var irrelevant = """{"type":"response_item","payload":{"type":"message"}}""" + "\n";
        await File.AppendAllTextAsync(path, irrelevant);

        var result = await monitor.ReadChangedFileAsync(path);

        Assert.Equal(Encoding.UTF8.GetByteCount(irrelevant), result.BytesRead);
        Assert.Equal(85, result.LatestSnapshot?.RemainingPercent);
    }

    [Fact]
    public async Task NewerOtherProductQuota_DoesNotReplaceCodexQuota()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("rollout-products.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.TokenCount(
                Start,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 20),
            TestLog.TokenCount(
                Start.AddMinutes(1),
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 99,
                limitId: "another-product"));
        var monitor = new RateLimitTailMonitor();

        var result = await monitor.ReadChangedFileAsync(path);

        Assert.Equal("codex", result.LatestSnapshot?.LimitId);
        Assert.Equal(80, result.LatestSnapshot?.RemainingPercent);
    }

    [Fact]
    public async Task Truncation_ResetsCheckpointAndQuota()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("rollout-truncated.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.IrrelevantHugeLine(16 * 1024),
            TestLog.TokenCount(
                Start,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 10));
        var monitor = new RateLimitTailMonitor();
        await monitor.ReadChangedFileAsync(path);
        await TestLog.WriteLinesAsync(
            path,
            TestLog.TokenCount(
                Start.AddMinutes(2),
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 60));

        var result = await monitor.ReadChangedFileAsync(path);

        Assert.Equal(Path.GetFullPath(path), Assert.Single(result.ResetPaths));
        Assert.Equal(40, result.LatestSnapshot?.RemainingPercent);
    }

    [Fact]
    public async Task SameLengthRewrite_ResetsCheckpointAndQuota()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("rollout-rewritten.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.TokenCount(
                Start,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 20));
        var monitor = new RateLimitTailMonitor();
        await monitor.ReadChangedFileAsync(path);
        var originalLength = new FileInfo(path).Length;

        await TestLog.WriteLinesAsync(
            path,
            TestLog.TokenCount(
                Start,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 80));
        Assert.Equal(originalLength, new FileInfo(path).Length);

        var result = await monitor.ReadChangedFileAsync(path);

        Assert.Equal(Path.GetFullPath(path), Assert.Single(result.ResetPaths));
        Assert.Equal(20, result.LatestSnapshot?.RemainingPercent);
    }

    [Fact]
    public async Task Rename_MovesCheckpoint_ThenReadsOnlyNewBytes()
    {
        using var temporary = new TemporaryDirectory();
        var oldPath = temporary.GetPath("sessions", "rollout.jsonl");
        var newPath = temporary.GetPath("archived", "rollout.jsonl");
        await TestLog.WriteLinesAsync(
            oldPath,
            TestLog.TokenCount(
                Start,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 20));
        var monitor = new RateLimitTailMonitor();
        await monitor.ReadChangedFileAsync(oldPath);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Move(oldPath, newPath);

        Assert.True(monitor.MoveCheckpoint(oldPath, newPath));
        var appended = TestLog.TokenCount(
            Start.AddMinutes(1),
            0,
            0,
            0,
            0,
            0,
            includeInfo: false,
            usedPercent: 35) + "\n";
        await File.AppendAllTextAsync(newPath, appended);
        var result = await monitor.ReadChangedFileAsync(newPath);

        Assert.False(monitor.TryGetCheckpoint(oldPath, out _));
        Assert.True(monitor.TryGetCheckpoint(newPath, out _));
        Assert.Equal(Encoding.UTF8.GetByteCount(appended), result.BytesRead);
        Assert.Equal(65, result.LatestSnapshot?.RemainingPercent);
    }

    [Fact]
    public async Task ForgetFile_RemovesDeletedFilesQuota()
    {
        using var temporary = new TemporaryDirectory();
        var first = temporary.GetPath("first.jsonl");
        var second = temporary.GetPath("second.jsonl");
        await TestLog.WriteLinesAsync(
            first,
            TestLog.TokenCount(
                Start,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 80));
        await TestLog.WriteLinesAsync(
            second,
            TestLog.TokenCount(
                Start.AddMinutes(1),
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 30));
        var monitor = new RateLimitTailMonitor();
        await monitor.ReadChangedFilesAsync([first, second]);

        Assert.True(monitor.ForgetFile(second));
        var result = await monitor.ReadChangedFilesAsync(Array.Empty<string>());

        Assert.Equal(20, result.LatestSnapshot?.RemainingPercent);
    }

    [Fact]
    public async Task Reset_ClearsAllCheckpoints()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("rollout-reset.jsonl");
        await TestLog.WriteLinesAsync(
            path,
            TestLog.TokenCount(
                Start,
                0,
                0,
                0,
                0,
                0,
                includeInfo: false,
                usedPercent: 20));
        var monitor = new RateLimitTailMonitor();
        await monitor.ReadChangedFileAsync(path);

        monitor.Reset();

        Assert.False(monitor.TryGetCheckpoint(path, out _));
        var calibrated = await monitor.ReadChangedFileAsync(path);
        Assert.Equal(80, calibrated.LatestSnapshot?.RemainingPercent);
        Assert.Empty(calibrated.ResetPaths);
    }
}
