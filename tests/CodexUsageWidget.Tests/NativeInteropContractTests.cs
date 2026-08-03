using System.Text.RegularExpressions;

namespace CodexUsageWidget.Tests;

public sealed class NativeInteropContractTests
{
    [Fact]
    public void DisplayPowerNotificationSafeHandleRetainsRegisteredHandle()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "CodexUsageWidget",
            "Services",
            "DisplayPowerMonitor.cs"));

        Assert.Contains("SetHandle(registeredHandle);", source);
        Assert.Contains(
            "UnregisterPowerSettingNotification(handle)",
            source);
    }

    [Fact]
    public void EveryDllImportIsRestrictedToSystem32()
    {
        var root = FindRepositoryRoot();
        var sourceFiles = Directory.GetFiles(
            Path.Combine(root, "src"),
            "*.cs",
            SearchOption.AllDirectories);
        var unguarded = new List<string>();
        var importPattern = new Regex(
            @"(?m)^\s*\[DllImport\(",
            RegexOptions.CultureInvariant);

        foreach (var path in sourceFiles)
        {
            var source = File.ReadAllText(path);
            foreach (Match import in importPattern.Matches(source))
            {
                var prefixStart = Math.Max(0, import.Index - 180);
                var prefix = source[prefixStart..import.Index];
                if (!prefix.Contains(
                        "[DefaultDllImportSearchPaths(" +
                        "DllImportSearchPath.System32)]",
                        StringComparison.Ordinal))
                {
                    var line = source[..import.Index].Count(
                        character => character == '\n') + 1;
                    unguarded.Add(
                        $"{Path.GetRelativePath(root, path)}:{line}");
                }
            }
        }

        Assert.True(
            unguarded.Count == 0,
            "Unrestricted native imports: " + string.Join(", ", unguarded));
    }

    [Fact]
    public void GdiBitmapFactoriesDisposeFailedResults()
    {
        var root = FindRepositoryRoot();
        var backdrop = File.ReadAllText(Path.Combine(
            root,
            "src",
            "CodexUsageWidget",
            "Services",
            "FrostedBackdropSnapshotService.cs"));
        var icon = File.ReadAllText(Path.Combine(
            root,
            "src",
            "CodexUsageWidget",
            "Services",
            "HollowLineIconRenderer.cs"));

        Assert.Contains("if (!captureCompleted)", backdrop);
        Assert.Contains("capturedBitmap?.Dispose();", backdrop);
        Assert.Equal(1, Count(backdrop, "capturedBitmap?.Dispose();"));
        Assert.Contains("if (!completed)", backdrop);
        Assert.Contains("result.Dispose();", backdrop);
        Assert.Contains("if (!completed)", icon);
        Assert.Contains("bitmap.Dispose();", icon);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexUsageWidget.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the CodexUsageWidget repository root.");
    }
}
