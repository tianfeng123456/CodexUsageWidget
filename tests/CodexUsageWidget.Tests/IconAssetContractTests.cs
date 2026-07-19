using System.Buffers.Binary;

namespace CodexUsageWidget.Tests;

public sealed class IconAssetContractTests
{
    [Fact]
    public void TrayIcon_UsesThemeAwareHollowLineRenderer()
    {
        string traySource = File.ReadAllText(
            FindRepositoryFile(
                "src/CodexUsageWidget/Services/TrayIconService.cs"));
        string rendererSource = File.ReadAllText(
            FindRepositoryFile(
                "src/CodexUsageWidget/Services/HollowLineIconRenderer.cs"));

        Assert.Contains(
            "HollowLineIconRenderer.CreateTrayIcon(",
            traySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "public void ApplyTheme(bool useLightTheme)",
            traySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "graphics.DrawEllipse(",
            rendererSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "graphics.Clear(Color.Transparent)",
            rendererSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FillEllipse(background",
            traySource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FillEllipse(center",
            traySource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationIcon_ContainsRequiredSmallSizes()
    {
        string project = File.ReadAllText(
            FindRepositoryFile(
                "src/CodexUsageWidget/CodexUsageWidget.csproj"));
        string iconPath = FindRepositoryFile(
            "src/CodexUsageWidget/Assets/CodexUsageWidget.ico");
        byte[] icon = File.ReadAllBytes(iconPath);

        Assert.Contains(
            "<ApplicationIcon>Assets\\CodexUsageWidget.ico</ApplicationIcon>",
            project,
            StringComparison.Ordinal);
        Assert.True(icon.Length > 1024);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(icon));
        Assert.Equal(
            (ushort)1,
            BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(2)));
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));
        Assert.True(count >= 7);

        var sizes = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var width = icon[6 + (index * 16)];
            sizes.Add(width == 0 ? 256 : width);
        }

        Assert.Contains(16, sizes);
        Assert.Contains(20, sizes);
        Assert.Contains(24, sizes);
        Assert.Contains(32, sizes);
        Assert.Contains(48, sizes);
        Assert.Contains(256, sizes);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {relativePath} from the test output directory.");
    }
}
