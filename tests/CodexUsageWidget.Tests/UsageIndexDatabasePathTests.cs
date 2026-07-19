using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class UsageIndexDatabasePathTests
{
    [Fact]
    public void ForHome_IsStableButIsolatesDifferentHomes()
    {
        using var temporary = new TemporaryDirectory();
        var appData = temporary.GetPath("app-data");
        var firstHome = temporary.GetPath("Codex", "Home");
        var equivalentHome = firstHome.ToUpperInvariant() +
                             Path.DirectorySeparatorChar;
        var otherHome = temporary.GetPath("Codex", "OtherHome");

        var first = UsageIndexDatabasePath.ForHome(appData, firstHome);
        var equivalent = UsageIndexDatabasePath.ForHome(
            appData,
            equivalentHome);
        var other = UsageIndexDatabasePath.ForHome(appData, otherHome);

        Assert.Equal(first, equivalent);
        Assert.NotEqual(first, other);
        Assert.Equal(Path.GetFullPath(appData), Path.GetDirectoryName(first));
        Assert.Matches(@"^usage-index-[0-9A-F]{64}\.db$", Path.GetFileName(first));
    }
}
