using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class GlassTransparencyPolicyTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public void Normalize_ClampsToSupportedRange(int value, int expected)
    {
        Assert.Equal(expected, GlassTransparencyPolicy.Normalize(value));
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(0, 50)]
    [InlineData(1, 51)]
    [InlineData(50, 75)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public void MigrateLegacyPercent_PreservesOldAppearanceOnNewUpperHalf(
        int legacyValue,
        int expected)
    {
        Assert.Equal(
            expected,
            GlassTransparencyPolicy.MigrateLegacyPercent(legacyValue));
    }

    [Fact]
    public void DefaultPercent_IsOriginalGlassAppearance()
    {
        Assert.Equal(50, GlassTransparencyPolicy.DefaultPercent);
        Assert.Equal(
            GlassTransparencyPolicy.LegacyGlassPercent,
            GlassTransparencyPolicy.DefaultPercent);
    }

    [Theory]
    [InlineData(0, 1d)]
    [InlineData(25, 1d)]
    [InlineData(50, 1d)]
    [InlineData(75, 0.50995d)]
    [InlineData(100, 0.0199d)]
    public void ToSurfaceOpacityFactor_FadesOnlyAboveOriginalGlassPosition(
        int transparencyPercent,
        double expected)
    {
        Assert.Equal(
            expected,
            GlassTransparencyPolicy.ToSurfaceOpacityFactor(
                transparencyPercent),
            precision: 10);
    }

    [Theory]
    [InlineData(0, 255)]
    [InlineData(25, 241)]
    [InlineData(50, 226)]
    [InlineData(75, 226)]
    [InlineData(100, 226)]
    public void ToSurfaceColorAlpha_BlendsSolidToOriginalThemeAlpha(
        int transparencyPercent,
        int expected)
    {
        Assert.Equal(
            (byte)expected,
            GlassTransparencyPolicy.ToSurfaceColorAlpha(
                originalAlpha: 226,
                transparencyPercent));
    }

    [Theory]
    [InlineData(0, 0d)]
    [InlineData(25, 0.5d)]
    [InlineData(50, 1d)]
    [InlineData(75, 0.50995d)]
    [InlineData(100, 0.0199d)]
    public void ToBackdropOpacityFactor_UsesSolidGlassAndSafeEndpoint(
        int transparencyPercent,
        double expected)
    {
        Assert.Equal(
            expected,
            GlassTransparencyPolicy.ToBackdropOpacityFactor(
                transparencyPercent),
            precision: 10);
    }
}
