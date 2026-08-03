using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class LocalDiagnosticLogTests
{
    [Fact]
    public void TryWrite_AppendsLocallyAndRotatesBoundedHistory()
    {
        using var temporary = new TemporaryDirectory();
        var directory = temporary.GetPath("diagnostics");

        Assert.True(LocalDiagnosticLog.TryWrite(
            directory,
            "first-operation",
            new InvalidOperationException("first failure"),
            maximumBytes: 1));
        Assert.True(LocalDiagnosticLog.TryWrite(
            directory,
            "second-operation",
            new IOException("second failure"),
            maximumBytes: 1));

        var current = File.ReadAllText(Path.Combine(directory, "diagnostics.log"));
        var previous = File.ReadAllText(
            Path.Combine(directory, "diagnostics.log.previous"));
        Assert.Contains("second-operation", current, StringComparison.Ordinal);
        Assert.Contains("second failure", current, StringComparison.Ordinal);
        Assert.Contains("first-operation", previous, StringComparison.Ordinal);
        Assert.Contains("first failure", previous, StringComparison.Ordinal);
    }

    [Fact]
    public void TryWrite_InvalidInputNeverThrows()
    {
        Assert.False(LocalDiagnosticLog.TryWrite(
            string.Empty,
            "operation",
            new InvalidOperationException("failure")));
        Assert.False(LocalDiagnosticLog.TryWrite(
            Path.GetTempPath(),
            string.Empty,
            new InvalidOperationException("failure")));
    }
}
