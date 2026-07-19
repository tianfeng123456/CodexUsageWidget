using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class CodexHomeLocatorTests
{
    [Fact]
    public void Detect_UsesExplicitValidPath()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.GetPath("sessions"));

        var result = CodexHomeLocator.Detect(temporary.Path);

        Assert.Equal(
            System.IO.Path.GetFullPath(temporary.Path),
            result.HomeDirectory);
        Assert.True(result.HasAnyDataSource);
    }

    [Fact]
    public void FromHome_BuildsAllKnownSourcePaths()
    {
        using var temporary = new TemporaryDirectory();

        var result = CodexHomePaths.FromHome(temporary.Path);

        Assert.Equal(
            temporary.GetPath("sessions"),
            result.SessionsDirectory);
        Assert.Equal(
            temporary.GetPath("archived_sessions"),
            result.ArchivedSessionsDirectory);
        Assert.Equal(
            temporary.GetPath("session_index.jsonl"),
            result.SessionIndexPath);
    }
}
