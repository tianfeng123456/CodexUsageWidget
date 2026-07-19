using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CodexUsageWidget.Core;

namespace CodexUsageWidget.Services;

internal static class HollowLineIconRenderer
{
    private const int TrayIconSize = 32;

    public static Icon CreateTrayIcon(
        double? remainingPercent,
        bool useLightTheme)
    {
        using var bitmap = CreateBitmap(
            TrayIconSize,
            remainingPercent,
            useLightTheme);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    internal static Bitmap CreateBitmap(
        int size,
        double? remainingPercent,
        bool useLightTheme)
    {
        var bitmap = new Bitmap(
            size,
            size,
            PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.Clear(Color.Transparent);

        var scale = size / 32f;
        var center = size / 2f;
        var lineColor = useLightTheme
            ? Color.FromArgb(245, 24, 29, 27)
            : Color.FromArgb(245, 241, 245, 243);
        using var linePen = new Pen(lineColor, Math.Max(1.15f, 2.05f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        var loopWidth = 18.5f * scale;
        var loopHeight = 8.8f * scale;
        foreach (var angle in new[] { 0f, 60f, 120f })
        {
            var state = graphics.Save();
            graphics.TranslateTransform(center, center);
            graphics.RotateTransform(angle);
            graphics.DrawEllipse(
                linePen,
                -loopWidth / 2,
                -loopHeight / 2,
                loopWidth,
                loopHeight);
            graphics.Restore(state);
        }

        // Preserve a clean hollow aperture after the three loops cross.
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using (var aperture = new SolidBrush(Color.Transparent))
        {
            var apertureSize = Math.Max(2.2f, 4.2f * scale);
            graphics.FillEllipse(
                aperture,
                center - (apertureSize / 2),
                center - (apertureSize / 2),
                apertureSize,
                apertureSize);
        }

        graphics.CompositingMode = CompositingMode.SourceOver;
        if (remainingPercent is { } remaining && double.IsFinite(remaining))
        {
            var status = RemainingQuotaStatusPolicy.Evaluate(
                remaining,
                isRefreshing: false,
                hasLiveData: true);
            var progressColor = status switch
            {
                RemainingQuotaStatus.Low =>
                    useLightTheme
                        ? Color.FromArgb(255, 154, 82, 0)
                        : Color.FromArgb(255, 255, 205, 114),
                RemainingQuotaStatus.NearlyExhausted =>
                    useLightTheme
                        ? Color.FromArgb(255, 163, 58, 0)
                        : Color.FromArgb(255, 255, 160, 82),
                RemainingQuotaStatus.Exhausted =>
                    useLightTheme
                        ? Color.FromArgb(255, 180, 35, 59)
                        : Color.FromArgb(255, 255, 115, 123),
                _ =>
                    useLightTheme
                        ? Color.FromArgb(255, 8, 122, 75)
                        : Color.FromArgb(255, 107, 224, 173),
            };
            using var progressPen = new Pen(
                progressColor,
                Math.Max(1.1f, 1.75f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            var inset = 2.4f * scale;
            graphics.DrawArc(
                progressPen,
                inset,
                inset,
                size - (2 * inset),
                size - (2 * inset),
                -82,
                58);
        }

        return bitmap;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
