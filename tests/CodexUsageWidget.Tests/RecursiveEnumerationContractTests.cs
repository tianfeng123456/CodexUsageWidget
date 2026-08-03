namespace CodexUsageWidget.Tests;

public sealed class RecursiveEnumerationContractTests
{
    [Fact]
    public void IndexEnumeration_TracksFailuresAndSkipsReparsePoints()
    {
        string source = ReadRepositoryFile(
            "src/CodexUsageWidget.Core/UsageIndexService.cs");

        Assert.Contains(
            "SearchOption.TopDirectoryOnly",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FileAttributes.ReparsePoint",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SessionFileEnumeration(ordered, hadFailures)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var hadFileReadFailure = enumeration.HadFailures",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SearchOption.AllDirectories",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecentQuotaEnumeration_SkipsReparsePointsAndInaccessibleDirectories()
    {
        string source = ReadRepositoryFile(
            "src/CodexUsageWidget/Services/DashboardController.cs");

        Assert.Contains("RecurseSubdirectories = true", source);
        Assert.Contains("IgnoreInaccessible = true", source);
        Assert.Contains("ReturnSpecialDirectories = false", source);
        Assert.Contains(
            "AttributesToSkip = FileAttributes.ReparsePoint",
            source);
        Assert.DoesNotContain("SearchOption.AllDirectories", source);
    }

    [Fact]
    public void ProductionSources_DoNotUseUnboundedRecursiveSearchOption()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourceRoot = Path.Combine(repositoryRoot, "src");
        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) &&
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(
                "SearchOption.AllDirectories",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "CodexUsageWidget.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }
}
