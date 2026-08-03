namespace CodexUsageWidget.Core;

/// <summary>
/// Pure managed blur operations for one-shot desktop backdrop snapshots.
/// </summary>
public static class BackdropBlur
{
    private const int BytesPerPixel = 4;

    /// <summary>
    /// Applies one or more clipped-edge box-blur passes to BGRA32 pixels.
    /// </summary>
    /// <param name="source">
    /// Source image bytes. The source is never modified.
    /// </param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="stride">
    /// Number of bytes per source row. It must be at least
    /// <c>width * 4</c>. Row padding is preserved.
    /// </param>
    /// <param name="radius">
    /// Radius of each horizontal and vertical pass. Zero returns an exact
    /// copy.
    /// </param>
    /// <param name="passes">
    /// Number of two-dimensional box-blur passes. Multiple passes approximate
    /// a smoother Gaussian blur without introducing a rendering loop.
    /// </param>
    /// <returns>
    /// A new buffer containing <c>stride * height</c> bytes.
    /// </returns>
    public static byte[] BlurBgra32(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int stride,
        int radius,
        int passes = 1)
    {
        ValidateArguments(
            source,
            width,
            height,
            stride,
            radius,
            passes,
            out var requiredLength);

        var current = source[..requiredLength].ToArray();
        if (radius == 0)
        {
            return current;
        }

        var temporary = new byte[requiredLength];
        for (var pass = 0; pass < passes; pass++)
        {
            BlurHorizontal(
                current,
                temporary,
                width,
                height,
                stride,
                Math.Min(radius, width - 1));
            BlurVertical(
                temporary,
                current,
                width,
                height,
                stride,
                Math.Min(radius, height - 1));
        }

        return current;
    }

    private static void ValidateArguments(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int stride,
        int radius,
        int passes,
        out int requiredLength)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Width must be greater than zero.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                "Height must be greater than zero.");
        }

        var minimumStride = checked(width * BytesPerPixel);
        if (stride < minimumStride)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stride),
                "Stride must contain every BGRA32 pixel in a row.");
        }

        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radius),
                "Radius cannot be negative.");
        }

        if (passes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(passes),
                "Pass count must be greater than zero.");
        }

        requiredLength = checked(stride * height);
        if (source.Length < requiredLength)
        {
            throw new ArgumentException(
                "The source buffer is smaller than stride * height.",
                nameof(source));
        }
    }

    private static void BlurHorizontal(
        byte[] source,
        byte[] destination,
        int width,
        int height,
        int stride,
        int radius)
    {
        Buffer.BlockCopy(source, 0, destination, 0, source.Length);
        if (radius == 0)
        {
            return;
        }

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            for (var channel = 0; channel < BytesPerPixel; channel++)
            {
                long sum = 0;
                for (var sampleX = 0; sampleX <= radius; sampleX++)
                {
                    sum += source[rowOffset + (sampleX * BytesPerPixel) + channel];
                }

                var sampleCount = radius + 1;
                for (var x = 0; x < width; x++)
                {
                    destination[rowOffset + (x * BytesPerPixel) + channel] =
                        RoundedByteAverage(sum, sampleCount);

                    var removeX = x - radius;
                    if (removeX >= 0)
                    {
                        sum -= source[
                            rowOffset +
                            (removeX * BytesPerPixel) +
                            channel];
                        sampleCount--;
                    }

                    var addX = x + radius + 1;
                    if (addX < width)
                    {
                        sum += source[
                            rowOffset +
                            (addX * BytesPerPixel) +
                            channel];
                        sampleCount++;
                    }
                }
            }
        }
    }

    private static void BlurVertical(
        byte[] source,
        byte[] destination,
        int width,
        int height,
        int stride,
        int radius)
    {
        Buffer.BlockCopy(source, 0, destination, 0, source.Length);
        if (radius == 0)
        {
            return;
        }

        for (var x = 0; x < width; x++)
        {
            var pixelOffset = x * BytesPerPixel;
            for (var channel = 0; channel < BytesPerPixel; channel++)
            {
                long sum = 0;
                for (var sampleY = 0; sampleY <= radius; sampleY++)
                {
                    sum += source[
                        (sampleY * stride) +
                        pixelOffset +
                        channel];
                }

                var sampleCount = radius + 1;
                for (var y = 0; y < height; y++)
                {
                    destination[
                        (y * stride) +
                        pixelOffset +
                        channel] = RoundedByteAverage(sum, sampleCount);

                    var removeY = y - radius;
                    if (removeY >= 0)
                    {
                        sum -= source[
                            (removeY * stride) +
                            pixelOffset +
                            channel];
                        sampleCount--;
                    }

                    var addY = y + radius + 1;
                    if (addY < height)
                    {
                        sum += source[
                            (addY * stride) +
                            pixelOffset +
                            channel];
                        sampleCount++;
                    }
                }
            }
        }
    }

    private static byte RoundedByteAverage(long sum, int count) =>
        (byte)((sum + (count / 2L)) / count);
}
