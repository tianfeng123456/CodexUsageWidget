using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class BackdropCaptureGeometryTests
{
    [Fact]
    public void FromDeviceIndependentBounds_ScalesCommonWidgetSizes()
    {
        var collapsed = BackdropCaptureGeometry.FromDeviceIndependentBounds(
            100,
            50,
            80,
            80,
            2,
            2);
        var expanded = BackdropCaptureGeometry.FromDeviceIndependentBounds(
            100,
            50,
            420,
            540,
            1.5,
            1.5);

        Assert.Equal(new CapturePixelRect(200, 100, 160, 160), collapsed);
        Assert.Equal(new CapturePixelRect(150, 75, 630, 810), expanded);
    }

    [Fact]
    public void FromDeviceIndependentBounds_RoundsOutwardAtFractionalEdges()
    {
        var result = BackdropCaptureGeometry.FromDeviceIndependentBounds(
            10.25,
            20.4,
            80.2,
            40.1,
            1.25,
            1.5);

        Assert.Equal(new CapturePixelRect(12, 30, 102, 61), result);
    }

    [Fact]
    public void FromDeviceIndependentBounds_SupportsNegativeMonitorCoordinates()
    {
        var result = BackdropCaptureGeometry.FromDeviceIndependentBounds(
            -100.2,
            -40.1,
            80,
            40,
            1.25,
            1.5);

        Assert.Equal(new CapturePixelRect(-126, -61, 101, 61), result);
    }

    [Fact]
    public void FromDeviceIndependentBounds_SupportsDifferentAxisScales()
    {
        var result = BackdropCaptureGeometry.FromDeviceIndependentBounds(
            8,
            10,
            20,
            30,
            1.25,
            2);

        Assert.Equal(new CapturePixelRect(10, 20, 25, 60), result);
    }

    [Theory]
    [InlineData(double.NaN, 0, 10, 10, 1, 1)]
    [InlineData(0, double.PositiveInfinity, 10, 10, 1, 1)]
    [InlineData(0, 0, 0, 10, 1, 1)]
    [InlineData(0, 0, -1, 10, 1, 1)]
    [InlineData(0, 0, 10, double.NaN, 1, 1)]
    [InlineData(0, 0, 10, 10, 0, 1)]
    [InlineData(0, 0, 10, 10, 1, double.NegativeInfinity)]
    public void FromDeviceIndependentBounds_RejectsInvalidValues(
        double left,
        double top,
        double width,
        double height,
        double scaleX,
        double scaleY)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BackdropCaptureGeometry.FromDeviceIndependentBounds(
                left,
                top,
                width,
                height,
                scaleX,
                scaleY));
    }

    [Fact]
    public void FromDeviceIndependentBounds_RejectsScaledCoordinateOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BackdropCaptureGeometry.FromDeviceIndependentBounds(
                int.MaxValue,
                0,
                1,
                1,
                2,
                1));
    }
}
