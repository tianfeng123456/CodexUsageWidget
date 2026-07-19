namespace CodexUsageWidget.Core;

/// <summary>
/// A device-pixel rectangle suitable for a desktop capture API.
/// </summary>
public readonly record struct CapturePixelRect(
    int X,
    int Y,
    int Width,
    int Height);

/// <summary>
/// Converts WPF-style device-independent window bounds into device pixels
/// without depending on WPF types.
/// </summary>
public static class BackdropCaptureGeometry
{
    /// <summary>
    /// Converts device-independent bounds to the smallest whole-pixel
    /// rectangle that completely contains them.
    /// </summary>
    /// <remarks>
    /// The left and top edges are rounded down while the right and bottom
    /// edges are rounded up. This is deliberate: rounding every component
    /// independently can omit a one-pixel strip at fractional DPI positions.
    /// Negative desktop coordinates are supported.
    /// </remarks>
    public static CapturePixelRect FromDeviceIndependentBounds(
        double left,
        double top,
        double width,
        double height,
        double dpiScaleX,
        double dpiScaleY)
    {
        ValidateFinite(left, nameof(left));
        ValidateFinite(top, nameof(top));
        ValidatePositiveFinite(width, nameof(width));
        ValidatePositiveFinite(height, nameof(height));
        ValidatePositiveFinite(dpiScaleX, nameof(dpiScaleX));
        ValidatePositiveFinite(dpiScaleY, nameof(dpiScaleY));

        var right = checked(left + width);
        var bottom = checked(top + height);

        var pixelLeft = Math.Floor(left * dpiScaleX);
        var pixelTop = Math.Floor(top * dpiScaleY);
        var pixelRight = Math.Ceiling(right * dpiScaleX);
        var pixelBottom = Math.Ceiling(bottom * dpiScaleY);

        var x = ToInt32(pixelLeft, nameof(left));
        var y = ToInt32(pixelTop, nameof(top));
        var rightEdge = ToInt32(pixelRight, nameof(width));
        var bottomEdge = ToInt32(pixelBottom, nameof(height));

        var pixelWidth = (long)rightEdge - x;
        var pixelHeight = (long)bottomEdge - y;
        if (pixelWidth is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "The scaled capture width must fit in a positive Int32.");
        }

        if (pixelHeight is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                "The scaled capture height must fit in a positive Int32.");
        }

        return new CapturePixelRect(
            x,
            y,
            (int)pixelWidth,
            (int)pixelHeight);
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The value must be finite.");
        }
    }

    private static void ValidatePositiveFinite(
        double value,
        string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The value must be finite and greater than zero.");
        }
    }

    private static int ToInt32(double value, string parameterName)
    {
        if (!double.IsFinite(value) ||
            value < int.MinValue ||
            value > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The scaled coordinate must fit in an Int32.");
        }

        return (int)value;
    }
}
