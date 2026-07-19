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
    [InlineData(0, 1d)]
    [InlineData(25, 0.7525d)]
    [InlineData(50, 0.505d)]
    [InlineData(75, 0.2575d)]
    [InlineData(99, 0.0199d)]
    [InlineData(100, 0.01d)]
    public void ToOpacityFactor_HigherTransparencyMeansLowerOpacity(
        int transparencyPercent,
        double expected)
    {
        Assert.Equal(
            expected,
            GlassTransparencyPolicy.ToOpacityFactor(transparencyPercent),
            precision: 10);
    }
}
