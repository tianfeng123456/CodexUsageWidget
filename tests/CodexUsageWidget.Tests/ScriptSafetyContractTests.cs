namespace CodexUsageWidget.Tests;

public sealed class ScriptSafetyContractTests
{
    [Fact]
    public void CaptureAudit_RestoresSettingsAndStopsOwnedProcessInFinally()
    {
        var source = ReadRepositoryFile("scripts/capture-ui.ps1");

        Assert.Contains("try {", source, StringComparison.Ordinal);
        Assert.Contains("finally {", source, StringComparison.Ordinal);
        Assert.Contains(
            "[IO.File]::WriteAllBytes(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Stop-Process -Id $script:AuditProcess.Id -Force",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$script:AuditSucceeded",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Could not restore prior UI evidence",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ui-audit-failed-",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-LeaveRunning is only safe with -TargetProcessId",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetInputDesktopName()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "UI audit requires the unlocked interactive Default desktop",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetForegroundProcessId()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$foregroundProcess.ProcessName -in @('LockApp', 'LogonUI')",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.IndexOf(
                "$foregroundProcessId =",
                StringComparison.Ordinal) <
            source.IndexOf(
                "$script:OriginalSettingsBytes = $null",
                StringComparison.Ordinal));
    }

    [Fact]
    public void BuildScript_ValidatesPublishDirectoryAtPathBoundary()
    {
        var source = ReadRepositoryFile("scripts/build.ps1");

        Assert.Contains(
            "$projectRootPrefix = $resolvedProjectRoot.TrimEnd(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$resolvedDist.StartsWith(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-Item -LiteralPath $resolvedDist -Recurse -Force",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scripts/audit-idle-styles.ps1")]
    [InlineData("scripts/audit-localization.ps1")]
    [InlineData("scripts/capture-themes.ps1")]
    [InlineData("scripts/capture-ui.ps1")]
    [InlineData("scripts/measure-idle.ps1")]
    public void UiAuditScripts_WriteTemporarySettingsWithoutUtf8Bom(
        string relativePath)
    {
        var source = ReadRepositoryFile(relativePath);

        Assert.DoesNotContain(
            "Set-Content -LiteralPath $SettingsPath -Encoding utf8",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Set-Content -LiteralPath $settingsPath -Encoding utf8",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "[Text.UTF8Encoding]::new($false)",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(FindRepositoryFile(relativePath));

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        var platformPath = relativePath.Replace(
            '/',
            Path.DirectorySeparatorChar);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, platformPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' from the test output directory.");
    }
}
