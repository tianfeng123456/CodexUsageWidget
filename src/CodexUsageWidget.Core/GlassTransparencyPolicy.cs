namespace CodexUsageWidget.Core;

public static class GlassTransparencyPolicy
{
    public const int MinimumPercent = 0;
    public const int MaximumPercent = 100;
    public const int DefaultPercent = 0;

    public static int Normalize(int transparencyPercent) =>
        Math.Clamp(
            transparencyPercent,
            MinimumPercent,
            MaximumPercent);

    public static double ToOpacityFactor(int transparencyPercent) =>
        1d - (Normalize(transparencyPercent) / 100d);
}
