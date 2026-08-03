using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodexUsageWidget.Tests;

public sealed class LocalizationResourceContractTests
{
    private const string EnglishResourcePath =
        "src/CodexUsageWidget/Resources/Strings.en.xaml";
    private const string ChineseResourcePath =
        "src/CodexUsageWidget/Resources/Strings.zh-Hans.xaml";
    private const string SettingsWindowPath =
        "src/CodexUsageWidget/SettingsWindow.xaml";
    private const string SettingsWindowCodeBehindPath =
        "src/CodexUsageWidget/SettingsWindow.xaml.cs";
    private const string AppSettingsPath =
        "src/CodexUsageWidget/Services/AppSettings.cs";
    private const string SettingsServicePath =
        "src/CodexUsageWidget/Services/SettingsService.cs";

    private static readonly string[] ProductionWindowPaths =
    [
        "src/CodexUsageWidget/MainWindow.xaml",
        "src/CodexUsageWidget/SettingsWindow.xaml",
    ];

    private static readonly string[] ProductionLocalizationCodePaths =
    [
        "src/CodexUsageWidget/App.xaml.cs",
        "src/CodexUsageWidget/SettingsWindow.xaml.cs",
        "src/CodexUsageWidget/Services/DashboardController.cs",
        "src/CodexUsageWidget/Services/TrayIconService.cs",
        "src/CodexUsageWidget/ViewModels/MainViewModel.cs",
        "src/CodexUsageWidget/ViewModels/UsageViewModels.cs",
    ];

    private static readonly string[] RequiredCapsuleStatusKeys =
    [
        "Loc.QuotaStatusSufficient",
        "Loc.UsageStable",
        "Loc.QuotaStatusLow",
        "Loc.QuotaStatusNearlyExhausted",
        "Loc.QuotaStatusExhausted",
        "Loc.QuotaStatusSyncing",
        "Loc.QuotaStatusWaitingForData",
    ];

    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Regex CompositeFormatPlaceholderPattern = new(
        @"(?<!\{)\{(?<index>\d+)[^{}]*\}(?!\})",
        RegexOptions.CultureInvariant);

    private static readonly Regex DynamicLocalizationReferencePattern = new(
        @"\{DynamicResource\s+(?<key>Loc\.[A-Za-z0-9_.-]+)\s*\}",
        RegexOptions.CultureInvariant);

    private static readonly Regex StaticLocalizationReferencePattern = new(
        @"\{StaticResource\s+(?<key>Loc\.[A-Za-z0-9_.-]+)\s*\}",
        RegexOptions.CultureInvariant);

    private static readonly Regex CjkPattern = new(
        @"[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]",
        RegexOptions.CultureInvariant);

    [Fact]
    public void LocalizedResourceDictionaries_HaveMatchingCompleteContracts()
    {
        Dictionary<string, string> english =
            LoadResourceDictionary(EnglishResourcePath);
        Dictionary<string, string> chinese =
            LoadResourceDictionary(ChineseResourcePath);

        Assert.Equal(
            english.Keys.Order(StringComparer.Ordinal),
            chinese.Keys.Order(StringComparer.Ordinal));

        Assert.All(
            english,
            entry => Assert.False(
                string.IsNullOrWhiteSpace(entry.Value),
                $"{EnglishResourcePath}: '{entry.Key}' has an empty value."));
        Assert.All(
            chinese,
            entry => Assert.False(
                string.IsNullOrWhiteSpace(entry.Value),
                $"{ChineseResourcePath}: '{entry.Key}' has an empty value."));

        foreach (string key in english.Keys)
        {
            string[] englishPlaceholders =
                GetPlaceholderSignature(english[key]);
            string[] chinesePlaceholders =
                GetPlaceholderSignature(chinese[key]);

            Assert.True(
                englishPlaceholders.SequenceEqual(
                    chinesePlaceholders,
                    StringComparer.Ordinal),
                $"Format placeholders differ for '{key}': " +
                $"English=[{string.Join(", ", englishPlaceholders)}], " +
                $"Chinese=[{string.Join(", ", chinesePlaceholders)}].");
        }

        Assert.All(
            RequiredCapsuleStatusKeys,
            key => Assert.True(
                english.ContainsKey(key),
                $"Missing required capsule status resource: {key}"));
    }

    [Fact]
    public void ProductionWindowXaml_UsesOnlyKnownDynamicLocalizationResources()
    {
        Dictionary<string, string> resources =
            LoadResourceDictionary(EnglishResourcePath);

        foreach (string relativePath in ProductionWindowPaths)
        {
            string content = File.ReadAllText(FindRepositoryFile(relativePath));
            string[] dynamicKeys = DynamicLocalizationReferencePattern
                .Matches(content)
                .Select(match => match.Groups["key"].Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] staticKeys = StaticLocalizationReferencePattern
                .Matches(content)
                .Select(match => match.Groups["key"].Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] missingKeys = dynamicKeys
                .Where(key => !resources.ContainsKey(key))
                .ToArray();

            Assert.NotEmpty(dynamicKeys);
            Assert.True(
                staticKeys.Length == 0,
                $"{relativePath} contains static localization resources: " +
                string.Join(", ", staticKeys));
            Assert.True(
                missingKeys.Length == 0,
                $"{relativePath} references missing localization resources: " +
                string.Join(", ", missingKeys));
        }
    }

    [Fact]
    public void ProductionWindowXaml_ContainsNoHardCodedCjkText()
    {
        foreach (string relativePath in ProductionWindowPaths)
        {
            string path = FindRepositoryFile(relativePath);
            string[] offendingLines = File.ReadLines(path)
                .Select(
                    (line, index) => new
                    {
                        Line = line,
                        LineNumber = index + 1,
                    })
                .Where(item => CjkPattern.IsMatch(item.Line))
                .Select(
                    item =>
                        $"{relativePath}:{item.LineNumber}: " +
                        item.Line.Trim())
                .ToArray();

            Assert.True(
                offendingLines.Length == 0,
                "Hard-coded CJK text remains in production XAML:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, offendingLines));
        }
    }

    [Fact]
    public void ProductionCode_UsesOnlyKnownLocalizationKeys()
    {
        Dictionary<string, string> resources =
            LoadResourceDictionary(EnglishResourcePath);
        string[] missingKeys = ProductionLocalizationCodePaths
            .SelectMany(
                relativePath => Regex.Matches(
                        File.ReadAllText(FindRepositoryFile(relativePath)),
                        @"Loc\.[A-Za-z0-9_.-]+",
                        RegexOptions.CultureInvariant)
                    .Select(match => match.Value))
            .Distinct(StringComparer.Ordinal)
            .Where(key => !resources.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missingKeys.Length == 0,
            "Production code references missing localization resources: " +
            string.Join(", ", missingKeys));
    }

    [Fact]
    public void SettingsWindow_ExposesStableCollapsedModeSelectorContract()
    {
        string content = File.ReadAllText(
            FindRepositoryFile(SettingsWindowPath));

        Assert.Contains(
            "AutomationProperties.AutomationId=\"CollapsedModeComboBox\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"{DynamicResource Loc.CollapsedModeGlow}\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains("Tag=\"Glow\"", content, StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"{DynamicResource Loc.CollapsedModeCircle}\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains("Tag=\"Circle\"", content, StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"{DynamicResource Loc.CollapsedModeCapsule}\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains("Tag=\"Capsule\"", content, StringComparison.Ordinal);
        Assert.True(
            content.IndexOf("Tag=\"Glow\"", StringComparison.Ordinal) <
            content.IndexOf("Tag=\"Circle\"", StringComparison.Ordinal) &&
            content.IndexOf("Tag=\"Circle\"", StringComparison.Ordinal) <
            content.IndexOf("Tag=\"Capsule\"", StringComparison.Ordinal),
            "Idle style options must be ordered Glow, Circle, Capsule.");
        Assert.Contains(
            "Foreground=\"{Binding Foreground, " +
            "RelativeSource={RelativeSource AncestorType=ComboBox}}\"",
            content,
            StringComparison.Ordinal);

        int appearance = content.IndexOf(
            "Loc.Appearance",
            StringComparison.Ordinal);
        int resident = content.IndexOf(
            "Loc.ResidentBehavior",
            StringComparison.Ordinal);
        int dataSource = content.IndexOf(
            "Loc.DataSource",
            StringComparison.Ordinal);
        int maintenance = content.IndexOf(
            "Loc.Maintenance",
            StringComparison.Ordinal);
        Assert.True(
            appearance >= 0 &&
            appearance < resident &&
            resident < dataSource &&
            dataSource < maintenance,
            "Settings sections must be ordered Appearance, Resident behavior, " +
            "Data source, Maintenance.");
    }

    [Fact]
    public void SettingsWindow_ExposesGlassTransparencyContract()
    {
        string content = File.ReadAllText(
            FindRepositoryFile(SettingsWindowPath));
        string settings = File.ReadAllText(
            FindRepositoryFile(AppSettingsPath));
        string codeBehind = File.ReadAllText(
            FindRepositoryFile(SettingsWindowCodeBehindPath));

        Assert.Contains(
            "AutomationProperties.AutomationId=\"GlassTransparencySlider\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "Minimum\" Value=\"0\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "Maximum\" Value=\"100\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "Loc.GlassTransparencyDescription",
            content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GlassTransparencyPreviewSurface",
            content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GlassTransparencySlider_OnValueChanged",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "public int GlassTransparencyPercent",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "GlassTransparencyPolicy.Normalize(",
            settings,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GlassTransparencyPreviewChanged",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsService_MigratesLegacyGlassTransparencyOnlyOnce()
    {
        string settings = File.ReadAllText(
            FindRepositoryFile(AppSettingsPath));
        string service = File.ReadAllText(
            FindRepositoryFile(SettingsServicePath));

        Assert.Contains(
            "GlassTransparencySemanticsVersion",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"glassTransparencySemanticsVersion\"",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "GlassTransparencyPolicy.MigrateLegacyPercent(",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "GlassTransparencyPolicy.CurrentSemanticsVersion",
            service,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsService_UsesAtomicBackupRecoveryStorage()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "src/CodexUsageWidget/Services/SettingsService.cs"));

        Assert.Contains("AtomicJsonFileStorage.ReadAsync(", source);
        Assert.Contains("AtomicJsonFileStorage.WriteAsync(", source);
        Assert.Contains("primaryNeedsRepair", source);
        Assert.DoesNotContain(
            "File.Move(temporaryPath, SettingsPath, overwrite: true)",
            source);
    }

    [Fact]
    public void SettingsWindow_ExposesDisplayOffMonitoringPauseContract()
    {
        string content = File.ReadAllText(
            FindRepositoryFile(SettingsWindowPath));
        string settings = File.ReadAllText(
            FindRepositoryFile(AppSettingsPath));

        Assert.Contains(
            "AutomationProperties.AutomationId=" +
            "\"PauseMonitoringWhenDisplayOffCheckBox\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "Loc.PauseMonitoringWhenDisplayOffDescription",
            content,
            StringComparison.Ordinal);
        Assert.Matches(
            @"public\s+bool\s+PauseMonitoringWhenDisplayOff\s*" +
            @"\{\s*get;\s*set;\s*\}\s*=\s*true",
            settings);
    }

    [Fact]
    public void AppSettings_CollapsedModeDefaultsAndFallsBackToCircle()
    {
        string content = File.ReadAllText(
            FindRepositoryFile(AppSettingsPath));

        Assert.Matches(
            @"public\s+enum\s+CollapsedWidgetMode\s*\{\s*" +
            @"Glow\s*,\s*Circle\s*,\s*Capsule\s*,?\s*\}",
            content);
        Assert.Matches(
            @"public\s+string\s+CollapsedMode\s*\{\s*get;\s*set;\s*\}" +
            @"\s*=\s*nameof\(CollapsedWidgetMode\.Circle\)",
            content);
        Assert.Contains(
            "\"glow\" => CollapsedWidgetMode.Glow",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"capsule\" => CollapsedWidgetMode.Capsule",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "_ => CollapsedWidgetMode.Circle",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "CollapsedMode = CollapsedWidgetModePolicy.ToSettingValue(",
            content,
            StringComparison.Ordinal);
    }

    private static Dictionary<string, string>
        LoadResourceDictionary(string relativePath)
    {
        XDocument document = XDocument.Load(
            FindRepositoryFile(relativePath),
            LoadOptions.PreserveWhitespace);
        XElement root = Assert.IsType<XElement>(document.Root);
        var keyedValues = root
            .Elements()
            .Select(
                element => new
                {
                    Key = (string?)element.Attribute(XamlNamespace + "Key"),
                    Value = element.Value,
                })
            .Where(entry => entry.Key is not null)
            .Select(entry => (Key: entry.Key!, entry.Value))
            .ToArray();
        string[] duplicateKeys = keyedValues
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(keyedValues);
        Assert.True(
            duplicateKeys.Length == 0,
            $"{relativePath} contains duplicate keys: " +
            string.Join(", ", duplicateKeys));

        return keyedValues.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.Ordinal);
    }

    private static string[] GetPlaceholderSignature(string value) =>
        CompositeFormatPlaceholderPattern
            .Matches(value)
            .Select(
                match => int.Parse(
                    match.Groups["index"].Value,
                    System.Globalization.CultureInfo.InvariantCulture))
            .GroupBy(index => index)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToArray();

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string platformPath = relativePath.Replace(
            '/',
            Path.DirectorySeparatorChar);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, platformPath);
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
