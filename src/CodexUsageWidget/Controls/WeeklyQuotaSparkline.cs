using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using CodexUsageWidget.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace CodexUsageWidget.Controls;

/// <summary>
/// Lightweight retained-mode rendering for the seven observed weekly-quota
/// closing values. It redraws only when data or size changes and has no timer.
/// </summary>
public sealed class WeeklyQuotaSparkline : FrameworkElement
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable<WeeklyQuotaDayViewModel>),
            typeof(WeeklyQuotaSparkline),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnItemsSourceChanged));

    public static readonly DependencyProperty StrokeBrushProperty =
        DependencyProperty.Register(
            nameof(StrokeBrush),
            typeof(Brush),
            typeof(WeeklyQuotaSparkline),
            new FrameworkPropertyMetadata(
                Brushes.MediumSpringGreen,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(
            nameof(TrackBrush),
            typeof(Brush),
            typeof(WeeklyQuotaSparkline),
            new FrameworkPropertyMetadata(
                Brushes.DimGray,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(WeeklyQuotaSparkline),
            new FrameworkPropertyMetadata(
                1.25d,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DotRadiusProperty =
        DependencyProperty.Register(
            nameof(DotRadius),
            typeof(double),
            typeof(WeeklyQuotaSparkline),
            new FrameworkPropertyMetadata(
                2.25d,
                FrameworkPropertyMetadataOptions.AffectsRender));

    private INotifyCollectionChanged? observedCollection;

    public IEnumerable<WeeklyQuotaDayViewModel>? ItemsSource
    {
        get => (IEnumerable<WeeklyQuotaDayViewModel>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public Brush StrokeBrush
    {
        get => (Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double DotRadius
    {
        get => (double)GetValue(DotRadiusProperty);
        set => SetValue(DotRadiusProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var days = ItemsSource?.Take(7).ToArray() ?? [];
        var count = Math.Max(7, days.Length);
        var horizontalPadding = Math.Max(2d, DotRadius + 1d);
        var verticalPadding = Math.Max(2d, DotRadius + 1d);
        var usableWidth = Math.Max(0d, ActualWidth - horizontalPadding * 2d);
        var usableHeight = Math.Max(0d, ActualHeight - verticalPadding * 2d);
        var trackPen = CreatePen(TrackBrush, 1d);
        var strokePen = CreatePen(
            StrokeBrush,
            Math.Max(0.75d, StrokeThickness));

        var baselineY = ActualHeight - verticalPadding;
        drawingContext.DrawLine(
            trackPen,
            new Point(horizontalPadding, baselineY),
            new Point(ActualWidth - horizontalPadding, baselineY));

        Point? previous = null;
        for (var index = 0; index < count; index++)
        {
            var x = horizontalPadding +
                    usableWidth * (index + 0.5d) / count;
            if (index >= days.Length ||
                !days[index].IsObserved ||
                days[index].ClosingUsedPercent is not { } closing)
            {
                drawingContext.DrawEllipse(
                    null,
                    trackPen,
                    new Point(x, baselineY),
                    Math.Max(1.2d, DotRadius - 0.6d),
                    Math.Max(1.2d, DotRadius - 0.6d));
                previous = null;
                continue;
            }

            var clamped = Math.Clamp(closing, 0d, 100d);
            var y = verticalPadding +
                    usableHeight * (1d - clamped / 100d);
            var point = new Point(x, y);
            if (previous is { } before)
            {
                drawingContext.DrawLine(strokePen, before, point);
            }

            drawingContext.DrawEllipse(
                StrokeBrush,
                null,
                point,
                Math.Max(1.5d, DotRadius),
                Math.Max(1.5d, DotRadius));
            previous = point;
        }
    }

    private static void OnItemsSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var control = (WeeklyQuotaSparkline)dependencyObject;
        control.ObserveCollection(eventArgs.NewValue as INotifyCollectionChanged);
        control.InvalidateVisual();
    }

    private void ObserveCollection(INotifyCollectionChanged? collection)
    {
        if (observedCollection is not null)
        {
            observedCollection.CollectionChanged -= OnCollectionChanged;
        }

        observedCollection = collection;
        if (observedCollection is not null)
        {
            observedCollection.CollectionChanged += OnCollectionChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private static Pen CreatePen(Brush brush, double thickness) =>
        new(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
}
