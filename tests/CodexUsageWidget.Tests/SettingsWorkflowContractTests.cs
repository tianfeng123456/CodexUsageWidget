namespace CodexUsageWidget.Tests;

public sealed class SettingsWorkflowContractTests
{
    [Fact]
    public void CodexHomeChange_IsCommittedBeforeSettingsArePersisted()
    {
        var source = ReadRepositoryFile(
            "src/CodexUsageWidget/App.xaml.cs");
        var changeStart = source.IndexOf(
            "var homeChanged =",
            StringComparison.Ordinal);
        var changeCall = source.IndexOf(
            "await dashboard.ChangeCodexHomeAsync(",
            changeStart,
            StringComparison.Ordinal);
        var persistenceAfterChange = source.IndexOf(
            "await PersistSettingsAsync();",
            changeCall,
            StringComparison.Ordinal);

        Assert.True(changeStart >= 0);
        Assert.True(changeCall > changeStart);
        Assert.True(persistenceAfterChange > changeCall);
        Assert.Contains(
            "dashboard.CurrentCodexHome ?? previousHome",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!changed)",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "CodexUsageWidget.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
