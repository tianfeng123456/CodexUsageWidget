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

    [Fact]
    public void Detect_DoesNotFallBackWhenExplicitPathIsInvalid()
    {
        using var explicitHome = new TemporaryDirectory();
        using var fallbackHome = new TemporaryDirectory();
        Directory.CreateDirectory(fallbackHome.GetPath("sessions"));

        var exception = Assert.Throws<DirectoryNotFoundException>(() =>
            CodexHomeLocator.Detect(
                explicitHome.Path,
                [fallbackHome.Path]));

        Assert.Contains("设置的 Codex Home", exception.Message);
    }

    [Fact]
    public void BuildCandidates_SkipsBlankAndDeduplicatesCaseInsensitively()
    {
        using var temporary = new TemporaryDirectory();
        var candidate = temporary.GetPath("candidate");
        var duplicate = candidate.ToUpperInvariant();

        var candidates = CodexHomeLocator.BuildCandidates(
            candidate,
            [" ", duplicate, candidate]);

        Assert.Equal(
            1,
            candidates.Count(
                value => string.Equals(
                    value,
                    candidate,
                    StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(candidates, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void FromHome_TrimsAndExpandsEnvironmentVariables()
    {
        const string variable = "CODEX_WIDGET_TEST_HOME";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, System.IO.Path.GetTempPath());
            var result = CodexHomePaths.FromHome(
                "  " + System.IO.Path.Combine("%" + variable + "%", "codex-home-test") + "  ");
            Assert.Equal(System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "codex-home-test")), result.HomeDirectory);
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Fact]
    public void Detect_WrapsMalformedExplicitPathWithoutTryingFallbacks()
    {
        var exception = Assert.Throws<DirectoryNotFoundException>(
            () => CodexHomeLocator.Detect(new string('a', 40_000)));
        // Windows validates length in GetFullPath; Unix rejects the source
        // through Directory.Exists. Both must reject the explicit source.
        if (OperatingSystem.IsWindows()) Assert.IsType<PathTooLongException>(exception.InnerException);
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("archived_sessions")]
    [InlineData("session_index.jsonl")]
    public void HasAnyDataSource_AcceptsEverySupportedSource(string source)
    {
        using var temporary = new TemporaryDirectory();
        var target = temporary.GetPath(source);
        if (System.IO.Path.HasExtension(source))
        {
            File.WriteAllText(target, string.Empty);
        }
        else
        {
            Directory.CreateDirectory(target);
        }

        Assert.True(CodexHomePaths.FromHome(temporary.Path).HasAnyDataSource);
    }
}
