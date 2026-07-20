namespace CodexUsageWidget.Core;

public static class GlassTransparencyPolicy
{
    public const int MinimumPercent = 0;
    public const int MaximumPercent = 100;
    public const int DefaultPercent = 50;
    public const int LegacyGlassPercent = 50;
    public const int CurrentSemanticsVersion = 2;
    public const double MinimumOpacityFactor = 0.01d;
    public const int LegacySafeEndpointPercent = 99;
    public const double SafeEndpointOpacityFactor =
        1d -
        LegacySafeEndpointPercent / 100d *
        (1d - MinimumOpacityFactor);

    public static int Normalize(int transparencyPercent) =>
        Math.Clamp(
            transparencyPercent,
            MinimumPercent,
            MaximumPercent);

    /// <summary>
    /// Converts a value written using the original slider semantics to the
    /// current scale. The previous 0% appearance now lives at the 50% default.
    /// </summary>
    public static int MigrateLegacyPercent(int legacyTransparencyPercent) =>
        Normalize(
            DefaultPercent +
            (int)Math.Round(
                Math.Min(
                    Normalize(legacyTransparencyPercent),
                    LegacySafeEndpointPercent) *
                (MaximumPercent - DefaultPercent) /
                (double)LegacySafeEndpointPercent,
                MidpointRounding.AwayFromZero));

    /// <summary>
    /// Returns the opacity applied to the original glass gradient.
    /// Values from 0% through 50% keep the gradient brush fully opaque while
    /// its stop alpha is blended toward a solid surface. Above 50%, the
    /// original gradient fades to the exact old 99% safe endpoint.
    /// </summary>
    public static double ToSurfaceOpacityFactor(int transparencyPercent)
    {
        var normalized = Normalize(transparencyPercent);
        if (normalized <= LegacyGlassPercent)
        {
            return 1d;
        }

        var progress =
            (normalized - LegacyGlassPercent) /
            (double)(MaximumPercent - LegacyGlassPercent);
        return 1d -
               progress *
               (1d - SafeEndpointOpacityFactor);
    }

    /// <summary>
    /// Returns the alpha for a gradient stop before the brush-level opacity
    /// is applied. At 0% every stop is fully opaque; at 50% and above the
    /// original theme alpha is preserved.
    /// </summary>
    public static byte ToSurfaceColorAlpha(
        byte originalAlpha,
        int transparencyPercent)
    {
        var normalized = Normalize(transparencyPercent);
        if (normalized >= LegacyGlassPercent)
        {
            return originalAlpha;
        }

        var progress = normalized / (double)LegacyGlassPercent;
        return (byte)Math.Round(
            byte.MaxValue +
            (originalAlpha - byte.MaxValue) * progress,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Returns the factor applied to the captured desktop backdrop.
    /// A solid 0% surface does not show the backdrop; 50% exactly restores
    /// the original glass composition, and 100% exactly matches its old 99%
    /// endpoint.
    /// </summary>
    public static double ToBackdropOpacityFactor(int transparencyPercent)
    {
        var normalized = Normalize(transparencyPercent);
        if (normalized <= LegacyGlassPercent)
        {
            return normalized / (double)LegacyGlassPercent;
        }

        return ToSurfaceOpacityFactor(normalized);
    }
}
