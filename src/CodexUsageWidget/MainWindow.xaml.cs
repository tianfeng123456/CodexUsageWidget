using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using CodexUsageWidget.Core;
using CodexUsageWidget.Services;
using CodexUsageWidget.ViewModels;
using SystemColors = System.Windows.SystemColors;

namespace CodexUsageWidget;

public partial class MainWindow : Window
{
    public const double CircleCollapsedWidth = 80;
    public const double CapsuleCollapsedWidth = 208;
    // Compatibility alias for scripts and callers that mean the default
    // circle surface.
    public const double CollapsedWidth = CircleCollapsedWidth;
    public const double CollapsedHeight = 80;
    public const double ExpandedWidth = 420;
    public const double ExpandedHeight = 540;
    private const double BaseCollapsedBackdropOpacity = 0.24;
    private const double BaseExpandedBackdropOpacity = 0.16;

    private MainViewModel? _subscribedViewModel;
    private double? _collapsedAnchorLeft;
    private double? _collapsedAnchorTop;
    private double _expandedAnchorOffsetX;
    private double _expandedAnchorOffsetY;
    private bool _adjustingWindowPosition;
    private bool _isDragging;
    private DispatcherOperation? _pendingCollapseOperation;
    private IntPtr _dragWindowHandle;
    private NativePoint _dragStartCursor;
    private int _dragStartWindowLeft;
    private int _dragStartWindowTop;
    private bool _usesLightTheme;
    private bool _appearanceInitialized;
    private bool _highContrastEnabled;
    private CollapsedWidgetMode _collapsedMode =
        CollapsedWidgetMode.Circle;
    private int _glassTransparencyPercent =
        GlassTransparencyPolicy.DefaultPercent;
    private System.Windows.Media.Color[] _panelGradientBaseColors = [];
    private System.Windows.Media.Color[] _collapsedGradientBaseColors = [];
    private DispatcherOperation? _pendingBackdropRefreshOperation;
    private CancellationTokenSource? _backdropRefreshCancellation;
    private int _backdropRefreshGeneration;
    private bool _isDisplayDormant;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += MainWindow_OnDataContextChanged;
        LocationChanged += MainWindow_OnLocationChanged;
        Loaded += MainWindow_OnLoaded;
        SourceInitialized += MainWindow_OnSourceInitialized;
        DpiChanged += MainWindow_OnDpiChanged;

        if (DataContext is null)
        {
            DataContext = new MainViewModel();
        }
        else
        {
            SubscribeToViewModel(DataContext as MainViewModel);
        }

        var expanded = ViewModel?.IsExpanded == true;
        ApplyWindowState(expanded);
        SynchronizeVisualState(expanded);
    }

    public MainViewModel? ViewModel => DataContext as MainViewModel;

    public event EventHandler? RefreshRequested;

    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Raised once when a collapsed widget is expanded by a real pointer hover.
    /// Programmatic expansion (pinning, settings, tray actions) does not raise it.
    /// </summary>
    public event EventHandler? HoverExpanded;

    /// <summary>
    /// Raised for every period-tab click, including clicking the selected tab again.
    /// </summary>
    public event EventHandler<PeriodRefreshRequestedEventArgs>? PeriodRefreshRequested;

    public event EventHandler<WidgetPositionChangedEventArgs>? WidgetPositionChanged;

    public void SetViewModel(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
    }

    public void ApplyTheme(bool useLightTheme)
    {
        ApplyAppearance(useLightTheme);
    }

    public void ApplyLocalization(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
    }

    public CollapsedWidgetMode CollapsedMode => _collapsedMode;

    public int GlassTransparencyPercent => _glassTransparencyPercent;

    public double CurrentCollapsedWidth =>
        GetCollapsedWidth(_collapsedMode);

    public static double GetCollapsedWidth(CollapsedWidgetMode mode) =>
        mode == CollapsedWidgetMode.Capsule
            ? CapsuleCollapsedWidth
            : CircleCollapsedWidth;

    public void SetCollapsedMode(CollapsedWidgetMode mode)
    {
        var normalized = Enum.IsDefined(typeof(CollapsedWidgetMode), mode)
            ? mode
            : CollapsedWidgetMode.Circle;
        if (_collapsedMode == normalized)
        {
            return;
        }

        _collapsedMode = normalized;
        if (ViewModel?.IsExpanded == true)
        {
            ShowExpandedVisual();
            return;
        }

        ShowCollapsedVisual();
        ApplyWindowState(expanded: false);
    }

    public void SetGlassTransparencyPercent(int transparencyPercent)
    {
        var normalized = GlassTransparencyPolicy.Normalize(
            transparencyPercent);
        if (_glassTransparencyPercent == normalized)
        {
            return;
        }

        _glassTransparencyPercent = normalized;
        ApplyGlassTransparencyResources();
    }

    public void SetDisplayDormant(bool isDormant)
    {
        if (_isDisplayDormant == isDormant)
        {
            return;
        }

        _isDisplayDormant = isDormant;
        if (!isDormant)
        {
            QueueBackdropRefresh();
            return;
        }

        ++_backdropRefreshGeneration;
        CancelRunningBackdropRefresh();
        if (_pendingBackdropRefreshOperation is
            {
                Status: DispatcherOperationStatus.Pending,
            } pending)
        {
            pending.Abort();
        }

        _pendingBackdropRefreshOperation = null;
    }

    public void ApplyAppearance(bool useLightTheme)
    {
        var highContrastEnabled = SystemParameters.HighContrast;
        if (_appearanceInitialized &&
            _usesLightTheme == useLightTheme &&
            _highContrastEnabled == highContrastEnabled)
        {
            return;
        }

        _usesLightTheme = useLightTheme;
        _highContrastEnabled = highContrastEnabled;
        _appearanceInitialized = true;
        var visuals = WidgetSkinVisuals.Create(useLightTheme);

        foreach (var (key, color) in visuals.Colors)
        {
            Resources[key] = CreateBrush(color);
        }

        Resources["WarningBrush"] = CreateBrush("#F5C76F");
        Resources["UrgentBrush"] = CreateBrush(
            useLightTheme ? "#FFE68132" : "#FFFFA052");
        Resources["CriticalBrush"] = CreateBrush("#FF6B72");
        Resources["TransparentBrush"] = System.Windows.Media.Brushes.Transparent;
        _panelGradientBaseColors =
            visuals.PanelGradient.Select(ParseColor).ToArray();
        _collapsedGradientBaseColors =
            visuals.CollapsedGradient.Select(ParseColor).ToArray();
        Resources["PanelBackgroundBrush"] =
            CreateGradientBrush(visuals.PanelGradient, middleOffset: 0.48);
        Resources["CollapsedBackgroundBrush"] =
            CreateGradientBrush(visuals.CollapsedGradient, middleOffset: 0.52);

        Resources["BodyFontFamily"] =
            new System.Windows.Media.FontFamily(visuals.BodyFontFamily);
        Resources["DisplayFontFamily"] =
            new System.Windows.Media.FontFamily(visuals.DisplayFontFamily);
        Resources["NumericFontFamily"] =
            new System.Windows.Media.FontFamily(visuals.DisplayFontFamily);
        Resources["HeroFontWeight"] = FontWeights.Light;
        Resources["HeroFontSize"] = visuals.HeroFontSize;
        Resources["SummaryValueFontSize"] = visuals.SummaryValueFontSize;

        Resources["CollapsedCornerRadius"] =
            new CornerRadius(visuals.CollapsedCornerRadius);
        Resources["CapsuleCornerRadius"] =
            new CornerRadius(visuals.CapsuleCornerRadius);
        Resources["PanelCornerRadius"] =
            new CornerRadius(visuals.PanelCornerRadius);
        Resources["OverlayCornerRadius"] =
            new CornerRadius(visuals.OverlayCornerRadius);
        Resources["ToolTipCornerRadius"] =
            new CornerRadius(visuals.ToolTipCornerRadius);
        Resources["ArchiveBadgeCornerRadius"] =
            new CornerRadius(visuals.ArchiveBadgeCornerRadius);
        Resources["IconButtonCornerRadius"] =
            new CornerRadius(visuals.IconButtonCornerRadius);
        Resources["TrendButtonCornerRadius"] =
            new CornerRadius(visuals.TrendButtonCornerRadius);
        Resources["TabSelectionCornerRadius"] =
            new CornerRadius(visuals.TabSelectionCornerRadius);
        Resources["TabIndicatorCornerRadius"] =
            new CornerRadius(visuals.TabIndicatorCornerRadius);

        Resources["CollapsedShadowBlur"] = visuals.CollapsedShadowBlur;
        Resources["CollapsedShadowDepth"] = visuals.CollapsedShadowDepth;
        Resources["CollapsedShadowOpacity"] = visuals.CollapsedShadowOpacity;
        Resources["CapsuleShadowBlur"] = visuals.CapsuleShadowBlur;
        Resources["CapsuleShadowDepth"] = visuals.CapsuleShadowDepth;
        Resources["CapsuleShadowOpacity"] = visuals.CapsuleShadowOpacity;
        Resources["PanelShadowBlur"] = visuals.PanelShadowBlur;
        Resources["PanelShadowDepth"] = visuals.PanelShadowDepth;
        Resources["PanelShadowOpacity"] = visuals.PanelShadowOpacity;
        Resources["OverlayShadowBlur"] = visuals.OverlayShadowBlur;
        Resources["OverlayShadowDepth"] = visuals.OverlayShadowDepth;
        Resources["OverlayShadowOpacity"] = visuals.OverlayShadowOpacity;
        Resources["WidgetShadowColor"] = ParseColor(visuals.ShadowColor);

        Resources["TabIndicatorWidth"] = visuals.TabIndicatorWidth;
        Resources["TabIndicatorHeight"] = visuals.TabIndicatorHeight;
        Resources["RankingRowHeight"] = visuals.RankingRowHeight;
        Resources["RankingBarHeight"] = visuals.RankingBarHeight;
        Resources["RingStrokeThickness"] = visuals.RingStrokeThickness;
        Resources["ContentMargin"] = new Thickness(
            visuals.ContentInset,
            0,
            visuals.ContentInset,
            11);
        Resources["TabPanelMargin"] = new Thickness(
            Math.Max(12, visuals.ContentInset - 1),
            0,
            0,
            0);

        if (highContrastEnabled)
        {
            ApplyHighContrastResources();
        }

        ApplyGlassTransparencyResources();

        DisableNativeMaterial();
        QueueBackdropRefresh();
    }

    private void ApplyGlassTransparencyResources()
    {
        var backdropOpacityFactor = _highContrastEnabled
            ? 0d
            : GlassTransparencyPolicy.ToBackdropOpacityFactor(
                _glassTransparencyPercent);

        Resources["CollapsedBackdropOpacity"] =
            BaseCollapsedBackdropOpacity * backdropOpacityFactor;
        Resources["ExpandedBackdropOpacity"] =
            BaseExpandedBackdropOpacity * backdropOpacityFactor;

        if (_highContrastEnabled)
        {
            return;
        }

        if (Resources["CollapsedBackgroundBrush"] is
            LinearGradientBrush collapsedBrush)
        {
            ApplyGlassTransparency(
                collapsedBrush,
                _collapsedGradientBaseColors,
                _glassTransparencyPercent);
        }

        if (Resources["PanelBackgroundBrush"] is
            LinearGradientBrush panelBrush)
        {
            ApplyGlassTransparency(
                panelBrush,
                _panelGradientBaseColors,
                _glassTransparencyPercent);
        }
    }

    private static void ApplyGlassTransparency(
        LinearGradientBrush brush,
        IReadOnlyList<System.Windows.Media.Color> baseColors,
        int transparencyPercent)
    {
        if (brush.GradientStops.Count != baseColors.Count)
        {
            return;
        }

        brush.Opacity =
            GlassTransparencyPolicy.ToSurfaceOpacityFactor(
                transparencyPercent);
        for (var index = 0; index < baseColors.Count; index++)
        {
            var baseColor = baseColors[index];
            brush.GradientStops[index].Color =
                System.Windows.Media.Color.FromArgb(
                    GlassTransparencyPolicy.ToSurfaceColorAlpha(
                        baseColor.A,
                        transparencyPercent),
                    baseColor.R,
                    baseColor.G,
                    baseColor.B);
        }
    }

    private void ApplyHighContrastResources()
    {
        Resources["PrimaryTextBrush"] = SystemColors.WindowTextBrush;
        Resources["SecondaryTextBrush"] = SystemColors.WindowTextBrush;
        Resources["MutedTextBrush"] = SystemColors.GrayTextBrush;
        Resources["AccentBrush"] = SystemColors.HighlightBrush;
        Resources["GoodBrush"] = SystemColors.HighlightBrush;
        Resources["WarningBrush"] = SystemColors.HighlightBrush;
        Resources["UrgentBrush"] = SystemColors.HighlightBrush;
        Resources["CriticalBrush"] = SystemColors.HighlightBrush;
        Resources["CapsulePrimaryTextBrush"] = SystemColors.WindowTextBrush;
        Resources["CapsuleSecondaryTextBrush"] = SystemColors.WindowTextBrush;
        Resources["CapsuleMutedTextBrush"] = SystemColors.GrayTextBrush;
        Resources["CapsuleAccentBrush"] = SystemColors.HighlightBrush;
        Resources["CapsuleGoodBrush"] = SystemColors.HighlightBrush;
        Resources["CapsuleWarningBrush"] = SystemColors.HighlightBrush;
        Resources["CapsuleUrgentBrush"] = SystemColors.HighlightBrush;
        Resources["CapsuleCriticalBrush"] = SystemColors.HighlightBrush;
        Resources["PanelBrush"] = SystemColors.WindowBrush;
        Resources["PanelBackgroundBrush"] = SystemColors.WindowBrush;
        Resources["CollapsedBackgroundBrush"] = SystemColors.WindowBrush;
        Resources["ToolTipBackgroundBrush"] = SystemColors.WindowBrush;
        Resources["OverlayBackgroundBrush"] = SystemColors.WindowBrush;
        Resources["CollapsedBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["ExpandedBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["ToolTipBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["OverlayBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["RingTrackBrush"] = SystemColors.GrayTextBrush;
        Resources["BarTrackBrush"] = SystemColors.GrayTextBrush;
        Resources["QuotaBarTrackBrush"] = SystemColors.GrayTextBrush;
        Resources["LineFillBrush"] = SystemColors.HighlightBrush;
        Resources["SummaryDividerBrush"] = SystemColors.WindowTextBrush;
        Resources["TabSelectionBackgroundBrush"] =
            System.Windows.Media.Brushes.Transparent;
        Resources["OverlayScrimBrush"] =
            new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                0xB0,
                0,
                0,
                0));
        Resources["CollapsedShadowOpacity"] = 0d;
        Resources["CapsuleShadowOpacity"] = 0d;
        Resources["PanelShadowOpacity"] = 0d;
        Resources["OverlayShadowOpacity"] = 0d;
    }

    public void Expand()
    {
        CancelPendingCollapse();
        if (ViewModel is { } viewModel)
        {
            viewModel.IsExpanded = true;
        }
        else
        {
            ApplyWindowState(expanded: true);
            SynchronizeVisualState(expanded: true);
        }
    }

    public void Collapse(bool force = false)
    {
        CancelPendingCollapse();
        if (!force && ViewModel?.IsPinned == true)
        {
            return;
        }

        if (ViewModel is { } viewModel)
        {
            viewModel.IsWeeklyQuotaOverlayOpen = false;
            viewModel.HoveredWeeklyQuotaDay = null;
            viewModel.IsExpanded = false;
        }
        else
        {
            ShowCollapsedVisual();
            ApplyWindowState(expanded: false);
        }
    }

    public void SetPinned(bool isPinned)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.IsPinned = isPinned;
        if (isPinned)
        {
            Expand();
        }
    }

    public void SetAutoCollapse(bool enabled, int delayMilliseconds = 800)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.AutoCollapseEnabled = enabled;
        // delayMilliseconds remains in the signature for binary/settings compatibility.
        // The current interaction deliberately collapses on the next dispatcher turn.
        _ = delayMilliseconds;

        if (!enabled)
        {
            CancelPendingCollapse();
            return;
        }

        if (!_isDragging &&
            !IsMouseOver &&
            viewModel is { IsPinned: false, IsExpanded: true })
        {
            QueueAutoCollapse();
        }
    }

    public (double Left, double Top) GetPersistedPosition() =>
        (
            _collapsedAnchorLeft ?? Left,
            _collapsedAnchorTop ?? Top
        );

    public void RequestRefresh()
    {
        QueueBackdropRefresh();
        if (ViewModel?.RefreshCommand.CanExecute(null) == true)
        {
            ViewModel.RefreshCommand.Execute(null);
        }
    }

    public void RequestSettings()
    {
        if (ViewModel?.OpenSettingsCommand.CanExecute(null) == true)
        {
            ViewModel.OpenSettingsCommand.Execute(null);
        }
    }

    public void RecheckAutoCollapse()
    {
        QueueAutoCollapse();
    }

    private void Window_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        CancelPendingCollapse();
        var wasExpanded = ViewModel?.IsExpanded == true;
        if (!wasExpanded && ViewModel is { } viewModel)
        {
            viewModel.SelectedPeriod = viewModel.GetPeriod(UsagePeriodKind.Today);
        }

        Expand();
        if (!wasExpanded)
        {
            HoverExpanded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Window_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        QueueAutoCollapse();
    }

    private void Window_OnDeactivated(object? sender, EventArgs e)
    {
        QueueAutoCollapse();
    }

    private void Window_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            ViewModel is { IsWeeklyQuotaOverlayOpen: true } viewModel)
        {
            viewModel.IsWeeklyQuotaOverlayOpen = false;
            viewModel.HoveredWeeklyQuotaDay = null;
            e.Handled = true;
        }
    }

    private void WeeklyQuotaScrim_OnMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.IsWeeklyQuotaOverlayOpen = false;
            viewModel.HoveredWeeklyQuotaDay = null;
        }

        e.Handled = true;
    }

    private void WeeklyQuotaDay_OnMouseEnter(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: WeeklyQuotaDayViewModel day,
            } &&
            ViewModel is { } viewModel)
        {
            viewModel.HoveredWeeklyQuotaDay = day;
        }
    }

    private void WeeklyQuotaDay_OnMouseLeave(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: WeeklyQuotaDayViewModel day,
            } &&
            ViewModel is { } viewModel &&
            ReferenceEquals(viewModel.HoveredWeeklyQuotaDay, day))
        {
            viewModel.HoveredWeeklyQuotaDay = null;
        }
    }

    private void QueueAutoCollapse()
    {
        // A global button-up can occasionally be delivered while the HWND is
        // being moved across a work-area edge, before WPF routes the matching
        // preview/capture callback. Never let that stale capture suppress
        // auto-collapse indefinitely once the physical button is released.
        if (_isDragging && !IsLeftButtonPhysicallyPressed())
        {
            _isDragging = false;
            _dragWindowHandle = IntPtr.Zero;
            if (Mouse.Captured == this)
            {
                Mouse.Capture(null);
            }
        }

        if (_isDragging ||
            ViewModel is not { AutoCollapseEnabled: true, IsPinned: false, IsExpanded: true })
        {
            return;
        }

        if (_pendingCollapseOperation is
            {
                Status: DispatcherOperationStatus.Pending
                    or DispatcherOperationStatus.Executing,
            })
        {
            return;
        }

        _pendingCollapseOperation = Dispatcher.BeginInvoke(
            () =>
            {
                _pendingCollapseOperation = null;
                if (_isDragging ||
                    IsPointerInsideWindowBounds() ||
                    ViewModel is not
                    {
                        AutoCollapseEnabled: true,
                        IsPinned: false,
                        IsExpanded: true,
                    })
                {
                    return;
                }

                // The native cursor position is authoritative here. WPF can
                // leave IsMouseOver true for one routed-input turn after a
                // captured drag ends at a monitor edge, even though the
                // pointer is already outside the HWND.
                Collapse();
            },
            DispatcherPriority.Input);
    }

    private void CancelPendingCollapse()
    {
        if (_pendingCollapseOperation is
            {
                Status: DispatcherOperationStatus.Pending,
            } pending)
        {
            pending.Abort();
        }

        _pendingCollapseOperation = null;
    }

    private bool IsPointerInsideWindowBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        return handle != IntPtr.Zero &&
               GetCursorPos(out var cursor) &&
               GetWindowRect(handle, out var windowRect) &&
               cursor.X >= windowRect.Left &&
               cursor.X < windowRect.Right &&
               cursor.Y >= windowRect.Top &&
               cursor.Y < windowRect.Bottom;
    }

    private void DragSurface_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pointer = e.GetPosition(this);
        var isDragRegion =
            ViewModel?.IsExpanded != true ||
            pointer.Y <= 76;
        if (e.ChangedButton != MouseButton.Left ||
            !isDragRegion ||
            IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero ||
            !GetCursorPos(out _dragStartCursor) ||
            !GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        CancelPendingCollapse();
        e.Handled = true;
        _dragWindowHandle = handle;
        _dragStartWindowLeft = windowRect.Left;
        _dragStartWindowTop = windowRect.Top;
        _isDragging = Mouse.Capture(this);
        if (!_isDragging)
        {
            _dragWindowHandle = IntPtr.Zero;
        }
    }

    private void Window_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        if (!IsLeftButtonPhysicallyPressed())
        {
            EndWindowDrag();
            return;
        }

        UpdateWindowDragPosition();
        e.Handled = true;
    }

    private void Window_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        UpdateWindowDragPosition();
        EndWindowDrag();
        e.Handled = true;
    }

    private void Window_OnLostMouseCapture(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        _dragWindowHandle = IntPtr.Zero;
        QueueBackdropRefresh();
        QueueAutoCollapse();
    }

    private void EndWindowDrag()
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        _dragWindowHandle = IntPtr.Zero;
        if (Mouse.Captured == this)
        {
            Mouse.Capture(null);
        }

        QueueBackdropRefresh();
        QueueAutoCollapse();
    }

    private void UpdateWindowDragPosition()
    {
        if (_dragWindowHandle == IntPtr.Zero ||
            !GetCursorPos(out var cursor))
        {
            return;
        }

        // Keep both ends of the drag in physical screen pixels. Converting the
        // accumulated delta with the window's current DPI causes a jump at a
        // mixed-DPI monitor boundary when that DPI changes mid-drag.
        var targetLeft = Math.Clamp(
            (long)_dragStartWindowLeft + cursor.X - _dragStartCursor.X,
            int.MinValue,
            int.MaxValue);
        var targetTop = Math.Clamp(
            (long)_dragStartWindowTop + cursor.Y - _dragStartCursor.Y,
            int.MinValue,
            int.MaxValue);
        _ = SetWindowPos(
            _dragWindowHandle,
            IntPtr.Zero,
            (int)targetLeft,
            (int)targetTop,
            0,
            0,
            SetWindowPositionFlags);
    }

    private void UsageTabs_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var tabItem = FindVisualAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (tabItem?.DataContext is not UsagePeriodViewModel period)
        {
            return;
        }

        if (ViewModel is { } viewModel)
        {
            viewModel.SelectedPeriod = period;
        }

        PeriodRefreshRequested?.Invoke(
            this,
            new PeriodRefreshRequestedEventArgs(period.Kind));
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.Selector)
            {
                return true;
            }

            element = element is FrameworkContentElement contentElement
                ? contentElement.Parent
                : VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = element is FrameworkContentElement contentElement
                ? contentElement.Parent
                : VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private void MainWindow_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SubscribeToViewModel(e.NewValue as MainViewModel);
        var expanded = ViewModel?.IsExpanded == true;
        ApplyWindowState(expanded);
        SynchronizeVisualState(expanded);
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (double.IsFinite(Left) && double.IsFinite(Top))
        {
            _collapsedAnchorLeft = Left;
            _collapsedAnchorTop = Top;
            ClampCollapsedAnchorToWorkArea();
        }

        var expanded = ViewModel?.IsExpanded == true;
        ApplyWindowState(expanded);
        SynchronizeVisualState(expanded);
    }

    private void MainWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        DisableNativeMaterial();
        ApplyWindowShape(ViewModel?.IsExpanded == true);
    }

    private void MainWindow_OnDpiChanged(
        object sender,
        System.Windows.DpiChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                var expanded = ViewModel?.IsExpanded == true;
                ApplyWindowShape(expanded);
                QueueBackdropRefresh(expanded);
            },
            DispatcherPriority.Loaded);
    }

    private void DisableNativeMaterial()
    {
        // No native AccentPolicy or system backdrop is ever installed. Calling
        // AccentState.Disabled on an otherwise normal layered WPF window can
        // itself disturb the per-pixel alpha surface, so there is intentionally
        // nothing to undo here.
    }

    private void ApplyWindowShape(bool expanded)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // AllowsTransparency supplies the real per-pixel silhouette. A native
        // HRGN is intentionally not applied: on layered, per-monitor-DPI
        // windows it can suppress WPF hit testing after a DPI transition.
        _ = WindowMaterialHelper.ClearWindowRegion(handle);
        _ = expanded;
    }

    private void QueueBackdropRefresh(bool? expanded = null)
    {
        if (_isDisplayDormant)
        {
            return;
        }

        var targetExpanded = expanded ?? ViewModel?.IsExpanded == true;
        var targetCollapsedMode = _collapsedMode;
        var generation = ++_backdropRefreshGeneration;
        CancelRunningBackdropRefresh();

        if (_pendingBackdropRefreshOperation is
            {
                Status: DispatcherOperationStatus.Pending,
            } pending)
        {
            pending.Abort();
        }

        _pendingBackdropRefreshOperation = null;
        var target = targetExpanded
            ? ExpandedGlassBackdrop
            : targetCollapsedMode == CollapsedWidgetMode.Capsule
                ? CapsuleGlassBackdrop
                : CollapsedGlassBackdrop;

        if (_highContrastEnabled || !IsLoaded)
        {
            CollapsedGlassBackdrop.ImageSource = null;
            CapsuleGlassBackdrop.ImageSource = null;
            ExpandedGlassBackdrop.ImageSource = null;
            return;
        }

        // A moved snapshot is more distracting than the intentionally opaque
        // tint fallback. Clear it immediately, then replace it after the one
        // event-driven capture and background blur finish.
        target.ImageSource = null;
        _pendingBackdropRefreshOperation = Dispatcher.BeginInvoke(
            () =>
            {
                _pendingBackdropRefreshOperation = null;
                if (generation != _backdropRefreshGeneration)
                {
                    return;
                }

                var cancellation = new CancellationTokenSource();
                _backdropRefreshCancellation = cancellation;
                _ = RefreshBackdropAsync(
                    targetExpanded,
                    targetCollapsedMode,
                    generation,
                    cancellation);
            },
            DispatcherPriority.Background);
    }

    private async Task RefreshBackdropAsync(
        bool expanded,
        CollapsedWidgetMode collapsedMode,
        int generation,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var shape = expanded
                ? WidgetWindowShape.Expanded
                : collapsedMode == CollapsedWidgetMode.Capsule
                    ? WidgetWindowShape.Capsule
                    : WidgetWindowShape.Collapsed;
             var image = await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var snapshot =
                        FrostedBackdropSnapshotService.Capture(
                            handle,
                            shape,
                            cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return snapshot is null
                        ? null
                        : FrostedBackdropSnapshotService.CreateBlurredImage(
                            snapshot);
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (image is null)
            {
                return;
            }

            if (generation != _backdropRefreshGeneration ||
                (ViewModel?.IsExpanded == true) != expanded ||
                (!expanded && _collapsedMode != collapsedMode) ||
                !IsLoaded)
            {
                return;
            }

            if (expanded)
            {
                ExpandedGlassBackdrop.ImageSource = image;
            }
            else
            {
                if (collapsedMode == CollapsedWidgetMode.Capsule)
                {
                    CapsuleGlassBackdrop.ImageSource = image;
                }
                else
                {
                    CollapsedGlassBackdrop.ImageSource = image;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer visual state superseded this event-driven snapshot.
        }
        catch (ArgumentException)
        {
            // Unsupported/remote capture paths retain the themed glass tint.
        }
        catch (Win32Exception)
        {
        }
        catch (ExternalException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (OverflowException)
        {
        }
        finally
        {
            if (ReferenceEquals(_backdropRefreshCancellation, cancellation))
            {
                _backdropRefreshCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelRunningBackdropRefresh()
    {
        try
        {
            _backdropRefreshCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SubscribeToViewModel(MainViewModel? viewModel)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            _subscribedViewModel.RefreshRequested -= ViewModel_OnRefreshRequested;
            _subscribedViewModel.SettingsRequested -= ViewModel_OnSettingsRequested;
        }

        _subscribedViewModel = viewModel;

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
            _subscribedViewModel.RefreshRequested += ViewModel_OnRefreshRequested;
            _subscribedViewModel.SettingsRequested += ViewModel_OnSettingsRequested;
        }
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsExpanded):
                var expanded = ViewModel?.IsExpanded == true;
                if (expanded)
                {
                    ApplyWindowState(expanded: true);
                    ShowExpandedVisual();
                }
                else
                {
                    ShowCollapsedVisual();
                    ApplyWindowState(expanded: false);
                }
                break;
            case nameof(MainViewModel.IsPinned):
                if (ViewModel?.IsPinned == true)
                {
                    Expand();
                }
                else if (!IsMouseOver)
                {
                    QueueAutoCollapse();
                }
                break;
            case nameof(MainViewModel.IsWeeklyQuotaOverlayOpen):
                if (ViewModel?.IsWeeklyQuotaOverlayOpen != true &&
                    ViewModel is { } viewModel)
                {
                    viewModel.HoveredWeeklyQuotaDay = null;
                }
                break;
        }
    }

    private void ViewModel_OnRefreshRequested(object? sender, EventArgs e)
    {
        QueueBackdropRefresh();
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ViewModel_OnSettingsRequested(object? sender, EventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyWindowState(bool expanded)
    {
        var collapsedWidth = CurrentCollapsedWidth;
        if (expanded &&
            Width <= collapsedWidth + 0.5 &&
            double.IsFinite(Left) &&
            double.IsFinite(Top))
        {
            _collapsedAnchorLeft = Left;
            _collapsedAnchorTop = Top;
        }

        var targetWidth = expanded ? ExpandedWidth : collapsedWidth;
        var targetHeight = expanded ? ExpandedHeight : CollapsedHeight;

        var suppressLocationChanges = IsLoaded;
        if (suppressLocationChanges)
        {
            _adjustingWindowPosition = true;
        }

        try
        {
            double? targetLeft = null;
            double? targetTop = null;
            if (IsLoaded)
            {
                if (expanded)
                {
                    var anchorLeft = _collapsedAnchorLeft ?? Left;
                    var anchorTop = _collapsedAnchorTop ?? Top;
                    var workArea = GetMonitorWorkArea(
                        anchorLeft,
                        anchorTop);
                    targetLeft = GetExpandedLeft(
                        anchorLeft,
                        workArea,
                        collapsedWidth);
                    targetTop = GetExpandedTop(
                        anchorTop,
                        workArea,
                        CollapsedHeight);
                    _expandedAnchorOffsetX = anchorLeft - targetLeft.Value;
                    _expandedAnchorOffsetY = anchorTop - targetTop.Value;
                }
                else if (_collapsedAnchorLeft is { } anchorLeft &&
                         _collapsedAnchorTop is { } anchorTop)
                {
                    var workArea = GetMonitorWorkArea(
                        anchorLeft,
                        anchorTop);
                    targetLeft = Math.Clamp(
                        anchorLeft,
                        workArea.Left,
                        Math.Max(
                            workArea.Left,
                            workArea.Right - collapsedWidth));
                    targetTop = Math.Clamp(
                        anchorTop,
                        workArea.Top,
                        Math.Max(
                            workArea.Top,
                            workArea.Bottom - CollapsedHeight));
                    _collapsedAnchorLeft = targetLeft;
                    _collapsedAnchorTop = targetTop;
                }
            }

            ApplyWindowBounds(
                targetWidth,
                targetHeight,
                targetLeft,
                targetTop);
        }
        finally
        {
            if (suppressLocationChanges)
            {
                _adjustingWindowPosition = false;
            }
        }

        ApplyWindowShape(expanded);
        QueueBackdropRefresh(expanded);
    }

    private void ApplyWindowBounds(
        double targetWidth,
        double targetHeight,
        double? targetLeft,
        double? targetTop)
    {
        if (!IsLoaded)
        {
            MinWidth = targetWidth;
            MaxWidth = targetWidth;
            MinHeight = targetHeight;
            MaxHeight = targetHeight;
            Width = targetWidth;
            Height = targetHeight;
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var nextLeft = targetLeft ?? Left;
        var nextTop = targetTop ?? Top;
        var dpi = VisualTreeHelper.GetDpi(this);
        NativeRect currentBounds = default;
        var nativeBoundsApplied =
            handle != IntPtr.Zero &&
            GetWindowRect(handle, out currentBounds);

        MinWidth = 0;
        MaxWidth = double.PositiveInfinity;
        MinHeight = 0;
        MaxHeight = double.PositiveInfinity;

        if (nativeBoundsApplied)
        {
            var physicalLeft = Math.Clamp(
                (long)currentBounds.Left +
                ClampToInt32((nextLeft - Left) * dpi.DpiScaleX),
                int.MinValue,
                int.MaxValue);
            var physicalTop = Math.Clamp(
                (long)currentBounds.Top +
                ClampToInt32((nextTop - Top) * dpi.DpiScaleY),
                int.MinValue,
                int.MaxValue);
            var physicalWidth = Math.Max(
                1,
                ClampToInt32(targetWidth * dpi.DpiScaleX));
            var physicalHeight = Math.Max(
                1,
                ClampToInt32(targetHeight * dpi.DpiScaleY));
            nativeBoundsApplied = SetWindowPos(
                handle,
                IntPtr.Zero,
                (int)physicalLeft,
                (int)physicalTop,
                physicalWidth,
                physicalHeight,
                SetWindowBoundsFlags);
        }

        Width = targetWidth;
        Height = targetHeight;
        Left = nextLeft;
        Top = nextTop;
        MinWidth = targetWidth;
        MaxWidth = targetWidth;
        MinHeight = targetHeight;
        MaxHeight = targetHeight;

        _ = nativeBoundsApplied;
    }

    private void SynchronizeVisualState(bool expanded)
    {
        if (expanded)
        {
            ShowExpandedVisual();
        }
        else
        {
            ShowCollapsedVisual();
        }
    }

    private void ShowExpandedVisual()
    {
        CollapsedHost.Visibility = Visibility.Collapsed;
        CollapsedHost.Opacity = 0;
        CapsuleHost.Visibility = Visibility.Collapsed;
        CapsuleHost.Opacity = 0;
        ExpandedPanel.Visibility = Visibility.Visible;
        ExpandedPanel.Opacity = 1;
    }

    private void ShowCollapsedVisual()
    {
        var showCapsule = _collapsedMode == CollapsedWidgetMode.Capsule;
        CollapsedHost.Visibility = showCapsule
            ? Visibility.Collapsed
            : Visibility.Visible;
        CollapsedHost.Opacity = showCapsule ? 0 : 1;
        CapsuleHost.Visibility = showCapsule
            ? Visibility.Visible
            : Visibility.Collapsed;
        CapsuleHost.Opacity = showCapsule ? 1 : 0;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.Opacity = 0;
    }

    private static double GetExpandedLeft(
        double anchorLeft,
        Rect workArea,
        double collapsedWidth)
    {
        var rightAlignedLeft = anchorLeft;
        var leftAlignedLeft =
            anchorLeft + collapsedWidth - ExpandedWidth;

        if (rightAlignedLeft + ExpandedWidth <= workArea.Right &&
            rightAlignedLeft >= workArea.Left)
        {
            return rightAlignedLeft;
        }

        if (leftAlignedLeft >= workArea.Left &&
            leftAlignedLeft + ExpandedWidth <= workArea.Right)
        {
            return leftAlignedLeft;
        }

        // Extremely small work areas or an off-screen legacy anchor: keep the
        // anchor unchanged and place as much of the panel on-screen as possible.
        var maximumLeft = Math.Max(workArea.Left, workArea.Right - ExpandedWidth);
        var clampedLeft = Math.Clamp(rightAlignedLeft, workArea.Left, maximumLeft);
        return clampedLeft;
    }

    private static double GetExpandedTop(
        double anchorTop,
        Rect workArea,
        double collapsedHeight)
    {
        var downAlignedTop = anchorTop;
        var upAlignedTop =
            anchorTop + collapsedHeight - ExpandedHeight;

        if (downAlignedTop + ExpandedHeight <= workArea.Bottom &&
            downAlignedTop >= workArea.Top)
        {
            return downAlignedTop;
        }

        if (upAlignedTop >= workArea.Top &&
            upAlignedTop + ExpandedHeight <= workArea.Bottom)
        {
            return upAlignedTop;
        }

        var maximumTop = Math.Max(workArea.Top, workArea.Bottom - ExpandedHeight);
        var clampedTop = Math.Clamp(downAlignedTop, workArea.Top, maximumTop);
        return clampedTop;
    }

    private void ClampCollapsedAnchorToWorkArea()
    {
        if (_collapsedAnchorLeft is not { } anchorLeft ||
            _collapsedAnchorTop is not { } anchorTop)
        {
            return;
        }

        var workArea = GetMonitorWorkArea(anchorLeft, anchorTop);
        var maximumLeft = Math.Max(
            workArea.Left,
            workArea.Right - CurrentCollapsedWidth);
        var maximumTop = Math.Max(
            workArea.Top,
            workArea.Bottom - CollapsedHeight);
        var clampedLeft = Math.Clamp(
            anchorLeft,
            workArea.Left,
            maximumLeft);
        var clampedTop = Math.Clamp(
            anchorTop,
            workArea.Top,
            maximumTop);
        var positionChanged =
            Math.Abs(clampedLeft - anchorLeft) > 0.01 ||
            Math.Abs(clampedTop - anchorTop) > 0.01;

        _collapsedAnchorLeft = clampedLeft;
        _collapsedAnchorTop = clampedTop;
        _adjustingWindowPosition = true;
        try
        {
            Left = clampedLeft;
            Top = clampedTop;
        }
        finally
        {
            _adjustingWindowPosition = false;
        }

        if (positionChanged)
        {
            WidgetPositionChanged?.Invoke(
                this,
                new WidgetPositionChangedEventArgs(
                    clampedLeft,
                    clampedTop));
        }
    }

    private Rect GetMonitorWorkArea(
        double anchorLeft,
        double anchorTop)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        var anchorCenter = PointToScreen(
            new System.Windows.Point(
                anchorLeft - Left + (CurrentCollapsedWidth / 2),
                anchorTop - Top + (CollapsedHeight / 2)));
        var monitor = MonitorFromPoint(
            new NativePoint(
                ClampToInt32(anchorCenter.X),
                ClampToInt32(anchorCenter.Y)),
            MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };

        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return SystemParameters.WorkArea;
        }

        // PointFromScreen preserves the monitor origin as well as the active
        // per-monitor DPI transform. Dividing absolute screen coordinates by
        // a scale factor breaks on negative-coordinate mixed-DPI monitors.
        var workAreaTopLeft = PointFromScreen(
            new System.Windows.Point(
                monitorInfo.WorkArea.Left,
                monitorInfo.WorkArea.Top));
        var workAreaBottomRight = PointFromScreen(
            new System.Windows.Point(
                monitorInfo.WorkArea.Right,
                monitorInfo.WorkArea.Bottom));
        return new Rect(
            Left + workAreaTopLeft.X,
            Top + workAreaTopLeft.Y,
            workAreaBottomRight.X - workAreaTopLeft.X,
            workAreaBottomRight.Y - workAreaTopLeft.Y);
    }

    private void MainWindow_OnLocationChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded ||
            WindowState != WindowState.Normal ||
            _adjustingWindowPosition)
        {
            return;
        }

        if (ViewModel?.IsExpanded == true)
        {
            // Preserve the edge chosen during automatic expansion. When a
            // right-edge or bottom-edge idle surface expands left/up, its
            // anchor is offset from the panel's top-left. Keeping that offset
            // while the user drags lets collapse restore the circle/capsule at
            // the corresponding panel edge instead of jumping to the panel's
            // new top-left corner.
            _collapsedAnchorLeft = Left + _expandedAnchorOffsetX;
            _collapsedAnchorTop = Top + _expandedAnchorOffsetY;
        }
        else
        {
            _collapsedAnchorLeft = Left;
            _collapsedAnchorTop = Top;
        }

        WidgetPositionChanged?.Invoke(
            this,
            new WidgetPositionChangedEventArgs(
                _collapsedAnchorLeft.Value,
                _collapsedAnchorTop.Value));
    }

    private void Window_OnClosed(object? sender, EventArgs e)
    {
        CancelPendingCollapse();
        ++_backdropRefreshGeneration;
        CancelRunningBackdropRefresh();
        if (_pendingBackdropRefreshOperation is
            {
                Status: DispatcherOperationStatus.Pending,
            } pending)
        {
            pending.Abort();
        }

        _pendingBackdropRefreshOperation = null;
        CollapsedGlassBackdrop.ImageSource = null;
        CapsuleGlassBackdrop.ImageSource = null;
        ExpandedGlassBackdrop.ImageSource = null;
        var handle = new WindowInteropHelper(this).Handle;
        _ = WindowMaterialHelper.ClearWindowRegion(handle);
        SubscribeToViewModel(null);
        DataContextChanged -= MainWindow_OnDataContextChanged;
        LocationChanged -= MainWindow_OnLocationChanged;
        Loaded -= MainWindow_OnLoaded;
        SourceInitialized -= MainWindow_OnSourceInitialized;
        DpiChanged -= MainWindow_OnDpiChanged;
    }

    private static LinearGradientBrush CreateGradientBrush(
        IReadOnlyList<string> colors,
        double middleOffset)
    {
        if (colors.Count != 3)
        {
            throw new ArgumentException(
                "A widget gradient must contain exactly three colors.",
                nameof(colors));
        }

        return new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            [
                new GradientStop(ParseColor(colors[0]), 0),
                new GradientStop(ParseColor(colors[1]), middleOffset),
                new GradientStop(ParseColor(colors[2]), 1),
            ],
        };
    }

    private static SolidColorBrush CreateBrush(string color) =>
        new(ParseColor(color));

    private static System.Windows.Media.Color ParseColor(string color) =>
        (System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString(color)!;

    private static bool IsLeftButtonPhysicallyPressed() =>
        (GetAsyncKeyState(VirtualKeyLeftButton) & 0x8000) != 0;

    private const int VirtualKeyLeftButton = 0x01;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SetWindowPositionFlags =
        SwpNoSize |
        SwpNoZOrder |
        SwpNoActivate;
    private const uint SetWindowBoundsFlags =
        SwpNoZOrder |
        SwpNoActivate;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    private static int ClampToInt32(double value) =>
        (int)Math.Clamp(
            Math.Round(value),
            int.MinValue,
            int.MaxValue);

}

public sealed class PeriodRefreshRequestedEventArgs(UsagePeriodKind period) : EventArgs
{
    public UsagePeriodKind Period { get; } = period;
}

public sealed class WidgetPositionChangedEventArgs(double left, double top) : EventArgs
{
    public double Left { get; } = left;

    public double Top { get; } = top;
}
