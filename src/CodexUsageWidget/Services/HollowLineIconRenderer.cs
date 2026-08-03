using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CodexUsageWidget.Core;

namespace CodexUsageWidget.Services;

internal static class HollowLineIconRenderer
{
    private const int TrayIconSize = 32;
    private const float DesignSize = 32f;

    internal enum VisualStatus
    {
        None,
        Good,
        Warning,
        Urgent,
        Critical,
    }

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
        var completed = false;
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.Clear(Color.Transparent);

        var scale = size / DesignSize;
        var center = size / 2f;
        var lineColor = useLightTheme
            ? Color.FromArgb(255, 16, 21, 18)
            : Color.FromArgb(255, 246, 248, 247);
        using var linePen = new Pen(lineColor, Math.Max(1.7f, 3f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        // Six rounded lobes share a small central aperture. The silhouette is
        // intentionally original rather than a reproduction of a third-party
        // mark, while the generous footprint and single hollow stroke retain
        // the visual clarity of a modern AI product icon at 16–20 px.
        using (var knot = new GraphicsPath())
        {
            PointF? previousEnd = null;
            PointF firstStart = default;
            for (var lobe = 0; lobe < 6; lobe++)
            {
                var radians = ((-90f + (lobe * 60f)) * MathF.PI) / 180f;
                var radialX = MathF.Cos(radians);
                var radialY = MathF.Sin(radians);
                var tangentX = -radialY;
                var tangentY = radialX;

                PointF Point(float radial, float tangent) =>
                    new(
                        center + ((radialX * radial + tangentX * tangent) * scale),
                        center + ((radialY * radial + tangentY * tangent) * scale));

                var start = Point(4.15f, -2.45f);
                var firstControl = Point(7.15f, -4.25f);
                var secondControl = Point(11.15f, -3.75f);
                var tip = Point(12.35f, 0f);
                var thirdControl = Point(11.15f, 3.75f);
                var fourthControl = Point(7.15f, 4.25f);
                var end = Point(4.15f, 2.45f);

                if (previousEnd is { } previous)
                {
                    knot.AddLine(previous, start);
                }
                else
                {
                    knot.StartFigure();
                    firstStart = start;
                }

                knot.AddBezier(
                    start,
                    firstControl,
                    secondControl,
                    tip);
                knot.AddBezier(
                    tip,
                    thirdControl,
                    fourthControl,
                    end);
                previousEnd = end;
            }

            if (previousEnd is { } finalEnd)
            {
                knot.AddLine(finalEnd, firstStart);
            }

            knot.CloseFigure();
            graphics.DrawPath(linePen, knot);
        }

        var visualStatus = GetVisualStatus(remainingPercent);
        if (visualStatus != VisualStatus.None)
        {
            var statusColor = visualStatus switch
            {
                VisualStatus.Warning =>
                    useLightTheme
                        ? Color.FromArgb(255, 154, 82, 0)
                        : Color.FromArgb(255, 255, 205, 114),
                VisualStatus.Urgent =>
                    useLightTheme
                        ? Color.FromArgb(255, 163, 58, 0)
                        : Color.FromArgb(255, 255, 160, 82),
                VisualStatus.Critical =>
                    useLightTheme
                        ? Color.FromArgb(255, 180, 35, 59)
                        : Color.FromArgb(255, 255, 115, 123),
                _ =>
                    useLightTheme
                        ? Color.FromArgb(255, 8, 122, 75)
                        : Color.FromArgb(255, 107, 224, 173),
            };
            var dotRadius = Math.Max(0.75f, 1.5f * scale);
            using var statusBrush = new SolidBrush(statusColor);
            graphics.FillEllipse(
                statusBrush,
                center + (11f * scale) - dotRadius,
                center - (11f * scale) - dotRadius,
                dotRadius * 2,
                dotRadius * 2);
        }

            completed = true;
            return bitmap;
        }
        finally
        {
            if (!completed)
            {
                bitmap.Dispose();
            }
        }
    }

    internal static VisualStatus GetVisualStatus(double? remainingPercent)
    {
        if (remainingPercent is not { } remaining ||
            !double.IsFinite(remaining))
        {
            return VisualStatus.None;
        }

        return RemainingQuotaStatusPolicy.Evaluate(
            remaining,
            isRefreshing: false,
            hasLiveData: true) switch
        {
            RemainingQuotaStatus.Low => VisualStatus.Warning,
            RemainingQuotaStatus.NearlyExhausted => VisualStatus.Urgent,
            RemainingQuotaStatus.Exhausted => VisualStatus.Critical,
            _ => VisualStatus.Good,
        };
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
