using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using CodexUsageWidget.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexUsageWidget.Controls;

public sealed class UsageRing : FrameworkElement
{
    private const double HeartbeatStartAngle = 74d;
    private const double HeartbeatEndAngle = 106d;
    private const double HeartbeatStartPercentage =
        (HeartbeatStartAngle + 90d) / 3.6d;
    private const double HeartbeatEndPercentage =
        (HeartbeatEndAngle + 90d) / 3.6d;

    public static readonly DependencyProperty PercentageProperty = DependencyProperty.Register(
        nameof(Percentage),
        typeof(double?),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(4d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GoodBrushProperty = DependencyProperty.Register(
        nameof(GoodBrush),
        typeof(Brush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.MediumSpringGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarningBrushProperty = DependencyProperty.Register(
        nameof(WarningBrush),
        typeof(Brush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.Gold, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CriticalBrushProperty = DependencyProperty.Register(
        nameof(CriticalBrush),
        typeof(Brush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.IndianRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UrgentBrushProperty = DependencyProperty.Register(
        nameof(UrgentBrush),
        typeof(Brush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.DarkOrange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush),
        typeof(Brush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize),
        typeof(double),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowTextProperty = DependencyProperty.Register(
        nameof(ShowText),
        typeof(bool),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowProgressDotProperty = DependencyProperty.Register(
        nameof(ShowProgressDot),
        typeof(bool),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowThicknessProperty = DependencyProperty.Register(
        nameof(GlowThickness),
        typeof(double),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Percentage
    {
        get => (double?)GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush GoodBrush
    {
        get => (Brush)GetValue(GoodBrushProperty);
        set => SetValue(GoodBrushProperty, value);
    }

    public Brush WarningBrush
    {
        get => (Brush)GetValue(WarningBrushProperty);
        set => SetValue(WarningBrushProperty, value);
    }

    public Brush CriticalBrush
    {
        get => (Brush)GetValue(CriticalBrushProperty);
        set => SetValue(CriticalBrushProperty, value);
    }

    public Brush UrgentBrush
    {
        get => (Brush)GetValue(UrgentBrushProperty);
        set => SetValue(UrgentBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public bool ShowText
    {
        get => (bool)GetValue(ShowTextProperty);
        set => SetValue(ShowTextProperty, value);
    }

    public bool ShowProgressDot
    {
        get => (bool)GetValue(ShowProgressDotProperty);
        set => SetValue(ShowProgressDotProperty, value);
    }

    public double GlowThickness
    {
        get => (double)GetValue(GlowThicknessProperty);
        set => SetValue(GlowThicknessProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var thickness = Math.Max(1, StrokeThickness);
        var glowThickness = Math.Max(0d, GlowThickness);
        var radius = Math.Max(
            0,
            Math.Min(ActualWidth, ActualHeight) / 2 -
            (thickness + glowThickness) / 2 -
            1);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var heartbeatPoints = CreateHeartbeatPoints(center, radius);
        var ringGeometry = CreateRingGeometry(center, radius, heartbeatPoints);

        var trackPen = CreatePen(TrackBrush, thickness);
        drawingContext.DrawGeometry(null, trackPen, ringGeometry);

        if (Percentage is { } rawPercentage && double.IsFinite(rawPercentage))
        {
            var percentage = Math.Clamp(rawPercentage, 0, 100);
            var progressBrush = GetProgressBrush(percentage);

            if (glowThickness > 0d)
            {
                drawingContext.PushOpacity(0.22d);
                _ = DrawProgress(
                    drawingContext,
                    center,
                    radius,
                    heartbeatPoints,
                    ringGeometry,
                    CreatePen(progressBrush, thickness + glowThickness),
                    percentage);
                drawingContext.Pop();
            }

            var progressPen = CreatePen(progressBrush, thickness);
            var progressEnd = DrawProgress(
                drawingContext,
                center,
                radius,
                heartbeatPoints,
                ringGeometry,
                progressPen,
                percentage);

            if (ShowProgressDot && progressEnd is { } statusPoint)
            {
                var dotRadius = Math.Max(1.8d, thickness * 0.78d);
                drawingContext.DrawEllipse(
                    progressBrush,
                    null,
                    statusPoint,
                    dotRadius,
                    dotRadius);
            }
        }

        if (ShowText)
        {
            DrawCenteredText(drawingContext, center);
        }
    }

    private void DrawCenteredText(DrawingContext drawingContext, Point center)
    {
        var text = Percentage is { } percentage && double.IsFinite(percentage)
            ? $"{RemainingQuotaStatusPolicy.ToDisplayPercentage(percentage)}%"
            : "--";
        var typeface = new Typeface(
            new FontFamily("Segoe UI Variable Display"),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            FontSize,
            TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        drawingContext.DrawText(
            formattedText,
            new Point(center.X - formattedText.Width / 2, center.Y - formattedText.Height / 2));
    }

    private Brush GetProgressBrush(double percentage)
    {
        return RemainingQuotaStatusPolicy.Evaluate(
            percentage,
            isRefreshing: false,
            hasLiveData: true) switch
        {
            RemainingQuotaStatus.Sufficient or
            RemainingQuotaStatus.Stable => GoodBrush,
            RemainingQuotaStatus.Low => WarningBrush,
            RemainingQuotaStatus.NearlyExhausted => UrgentBrush,
            _ => CriticalBrush,
        };
    }

    private static Point? DrawProgress(
        DrawingContext drawingContext,
        Point center,
        double radius,
        IReadOnlyList<Point> heartbeatPoints,
        Geometry ringGeometry,
        Pen progressPen,
        double percentage)
    {
        if (percentage <= 0)
        {
            return null;
        }

        var startPoint = PointOnCircle(center, radius, -90d);
        if (percentage >= 99.999d)
        {
            drawingContext.DrawGeometry(null, progressPen, ringGeometry);
            return startPoint;
        }

        var geometry = new StreamGeometry();
        Point progressEnd;
        using (var context = geometry.Open())
        {
            context.BeginFigure(startPoint, isFilled: false, isClosed: false);

            if (percentage < HeartbeatStartPercentage)
            {
                progressEnd = PointOnCircle(
                    center,
                    radius,
                    -90d + percentage * 3.6d);
                AppendArc(context, progressEnd, radius);
            }
            else
            {
                AppendArc(context, heartbeatPoints[0], radius);

                if (percentage < HeartbeatEndPercentage)
                {
                    var heartbeatProgress =
                        (percentage - HeartbeatStartPercentage) /
                        (HeartbeatEndPercentage - HeartbeatStartPercentage);
                    progressEnd = AppendPartialPolyline(
                        context,
                        heartbeatPoints,
                        heartbeatProgress);
                }
                else
                {
                    AppendPolyline(context, heartbeatPoints, startIndex: 1);
                    progressEnd = PointOnCircle(
                        center,
                        radius,
                        -90d + percentage * 3.6d);
                    AppendArc(context, progressEnd, radius);
                }
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, progressPen, geometry);
        return progressEnd;
    }

    private static StreamGeometry CreateRingGeometry(
        Point center,
        double radius,
        IReadOnlyList<Point> heartbeatPoints)
    {
        var startPoint = PointOnCircle(center, radius, -90d);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(startPoint, isFilled: false, isClosed: false);
            AppendArc(context, heartbeatPoints[0], radius);
            AppendPolyline(context, heartbeatPoints, startIndex: 1);
            AppendArc(context, startPoint, radius);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point[] CreateHeartbeatPoints(Point center, double radius)
    {
        if (radius < 12d)
        {
            var bottom = PointOnCircle(center, radius, 90d);
            return [bottom, bottom];
        }

        var rightConnection =
            PointOnCircle(center, radius, HeartbeatStartAngle);
        var leftConnection =
            PointOnCircle(center, radius, HeartbeatEndAngle);
        var baseline = (rightConnection.Y + leftConnection.Y) / 2d;

        // Clockwise progress reaches the bottom from right to left.
        return
        [
            rightConnection,
            new Point(center.X + 5d, baseline),
            new Point(center.X + 3d, baseline - 2d),
            new Point(center.X + 0.5d, baseline + 4d),
            new Point(center.X - 2d, baseline - 3d),
            new Point(center.X - 4d, baseline),
            leftConnection,
        ];
    }

    private static void AppendArc(
        StreamGeometryContext context,
        Point endPoint,
        double radius)
    {
        context.ArcTo(
            endPoint,
            new Size(radius, radius),
            rotationAngle: 0,
            isLargeArc: false,
            sweepDirection: SweepDirection.Clockwise,
            isStroked: true,
            isSmoothJoin: true);
    }

    private static void AppendPolyline(
        StreamGeometryContext context,
        IReadOnlyList<Point> points,
        int startIndex)
    {
        for (var index = startIndex; index < points.Count; index++)
        {
            context.LineTo(points[index], isStroked: true, isSmoothJoin: false);
        }
    }

    private static Point AppendPartialPolyline(
        StreamGeometryContext context,
        IReadOnlyList<Point> points,
        double progress)
    {
        var clampedProgress = Math.Clamp(progress, 0d, 1d);
        var totalLength = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            totalLength += Distance(points[index - 1], points[index]);
        }

        var remainingLength = totalLength * clampedProgress;
        var current = points[0];
        for (var index = 1; index < points.Count; index++)
        {
            var next = points[index];
            var segmentLength = Distance(current, next);
            if (remainingLength >= segmentLength)
            {
                context.LineTo(next, isStroked: true, isSmoothJoin: false);
                remainingLength -= segmentLength;
                current = next;
                continue;
            }

            if (segmentLength <= double.Epsilon)
            {
                current = next;
                continue;
            }

            var ratio = remainingLength / segmentLength;
            var partial = new Point(
                current.X + (next.X - current.X) * ratio,
                current.Y + (next.Y - current.Y) * ratio);
            if (remainingLength > 0d)
            {
                context.LineTo(
                    partial,
                    isStroked: true,
                    isSmoothJoin: false);
            }

            return partial;
        }

        return points[^1];
    }

    private static double Distance(Point first, Point second)
    {
        var deltaX = second.X - first.X;
        var deltaY = second.Y - first.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        return pen;
    }

    private static Point PointOnCircle(Point center, double radius, double angleInDegrees)
    {
        var radians = angleInDegrees * Math.PI / 180;
        return new Point(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }
}
