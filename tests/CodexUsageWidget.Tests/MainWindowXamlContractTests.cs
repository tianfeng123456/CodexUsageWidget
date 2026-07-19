using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodexUsageWidget.Tests;

public sealed class MainWindowXamlContractTests
{
    private static readonly Regex OneWayModePattern = new(
        @"(?:^|,)\s*Mode\s*=\s*OneWay\s*(?:,|})",
        RegexOptions.CultureInvariant);

    [Fact]
    public void WeeklyQuotaHoverRunBindings_AreExplicitlyOneWay()
    {
        XDocument document = XDocument.Load(FindMainWindowXaml());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        string[] hoveredDayBindings = document
            .Descendants(presentation + "Run")
            .Select(run => (string?)run.Attribute("Text"))
            .Where(text => text?.Contains(
                "Binding HoveredWeeklyQuotaDay.",
                StringComparison.Ordinal) == true)
            .Cast<string>()
            .ToArray();

        Assert.Equal(4, hoveredDayBindings.Length);
        Assert.All(
            hoveredDayBindings,
            binding => Assert.Matches(OneWayModePattern, binding));
    }

    [Fact]
    public void IdleSurfaces_AreStableAlternatives_WithoutTransitionStoryboard()
    {
        string xamlPath = FindMainWindowXaml();
        string xaml = File.ReadAllText(xamlPath);
        string codeBehind = File.ReadAllText(xamlPath + ".cs");

        Assert.Contains("x:Name=\"CollapsedHost\"", xaml);
        Assert.Contains("x:Name=\"CapsuleHost\"", xaml);
        Assert.Contains("x:Name=\"CapsuleGlassBackdrop\"", xaml);
        Assert.Contains("Width=\"208\"", xaml);
        Assert.Contains("Height=\"80\"", xaml);
        Assert.DoesNotContain("TransitionCapsule", xaml);
        Assert.DoesNotContain("TransitionCapsule", codeBehind);
        Assert.DoesNotContain("BeginOpeningVisual", codeBehind);
        Assert.DoesNotContain("Storyboard", codeBehind);
    }

    [Fact]
    public void CapsuleSurface_HasNoRectangularEffect_AndBackdropIsRounded()
    {
        XDocument document = XDocument.Load(FindMainWindowXaml());
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement capsuleCard = document
            .Descendants(presentation + "Border")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") == "CapsuleCard");
        XElement backdropSurface = capsuleCard
            .Descendants(presentation + "Border")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "CapsuleBackdropSurface");

        Assert.Empty(capsuleCard.Elements(presentation + "Border.Effect"));
        Assert.Equal(
            "{DynamicResource CapsuleCornerRadius}",
            (string?)backdropSurface.Attribute("CornerRadius"));
        Assert.Contains(
            backdropSurface.Descendants(presentation + "ImageBrush"),
            element =>
                (string?)element.Attribute(xaml + "Name") ==
                "CapsuleGlassBackdrop");
    }

    [Fact]
    public void CapsuleStatus_UsesDynamicTextAndSharedStateBrush()
    {
        string xaml = File.ReadAllText(FindMainWindowXaml());

        Assert.Contains(
            "Text=\"{Binding CapsuleQuotaStatusText, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Stroke=\"{Binding Foreground, ElementName=CapsuleStatusText, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Binding=\"{Binding CapsuleQuotaStatus, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "core:RemainingQuotaStatus.NearlyExhausted",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{DynamicResource CapsuleUrgentBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Foreground=\"{DynamicResource CapsulePrimaryTextBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Foreground=\"{DynamicResource CapsuleSecondaryTextBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{DynamicResource Loc.UsageStable}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UsageTabs_ExposeLocalizedAutomationNames()
    {
        string xaml = File.ReadAllText(FindMainWindowXaml());

        Assert.Contains(
            "<Setter Property=\"AutomationProperties.Name\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{Binding DisplayName}\"",
            xaml,
            StringComparison.Ordinal);
    }

    private static string FindMainWindowXaml()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "CodexUsageWidget",
                "MainWindow.xaml");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate src/CodexUsageWidget/MainWindow.xaml from the test output directory.");
    }
}
