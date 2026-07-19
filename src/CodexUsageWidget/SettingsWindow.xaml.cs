using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using CodexUsageWidget.Core;
using CodexUsageWidget.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using SystemColors = System.Windows.SystemColors;

namespace CodexUsageWidget;

public partial class SettingsWindow : Window
{
    private readonly AppSettings originalSettings;
    private bool systemEventsSubscribed;
    private bool initializing = true;

    public SettingsWindow(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        InitializeComponent();
        originalSettings = settings;

        CodexHomeTextBox.Text = settings.CodexHomePath;
        AlwaysOnTopCheckBox.IsChecked = settings.AlwaysOnTop;
        AutoCollapseCheckBox.IsChecked = settings.AutoCollapse;
        StartupCheckBox.IsChecked = settings.StartWithWindows;
        PauseMonitoringWhenDisplayOffCheckBox.IsChecked =
            settings.PauseMonitoringWhenDisplayOff;
        SelectTheme(ThemeService.ParseMode(settings.ThemeMode));
        SelectLanguage(AppLanguagePolicy.ParseMode(settings.LanguageMode));
        SelectCollapsedMode(
            CollapsedWidgetModePolicy.Parse(settings.CollapsedMode));
        GlassTransparencySlider.Value =
            GlassTransparencyPolicy.Normalize(
                settings.GlassTransparencyPercent);
        ApplyTheme(ThemeService.ShouldUseLightTheme(settings.ThemeMode));
        Language = XmlLanguage.GetLanguage(
            LocalizationService.Instance.Culture.IetfLanguageTag);
        initializing = false;
    }

    public AppSettings? ResultSettings { get; private set; }

    public bool RebuildIndexRequested { get; private set; }

    public event Action<int>? GlassTransparencyPreviewChanged;

    public void ApplyTheme(bool useLightTheme)
    {
        var colors = useLightTheme
            ? new Dictionary<string, string>
            {
                ["PrimaryBrush"] = "#242722",
                ["PanelBrush"] = "#FFF8F5F0",
                ["CardBrush"] = "#FFF1EEE8",
                ["BorderBrush"] = "#8C9A9F9A",
                ["MutedBrush"] = "#66706A",
                ["ControlBackgroundBrush"] = "#FFFEFCF8",
                ["ControlHoverBrush"] = "#FFE9EEE9",
                ["AccentBrush"] = "#FF249B68",
                ["AccentSoftBrush"] = "#20249B68",
                ["HeaderBackgroundBrush"] = "#FFF4F1EB",
                ["SeparatorBrush"] = "#CBD3CDC8",
                ["NeutralButtonBrush"] = "#FFE8ECE8",
                ["SaveButtonBrush"] = "#FF249B68",
                ["SaveTextBrush"] = "#FFFFFFFF",
                ["ToolTipBackgroundBrush"] = "#FFF9F6F1",
                ["ToolTipBorderBrush"] = "#6E8B938D",
            }
            : new Dictionary<string, string>
            {
                ["PrimaryBrush"] = "#F1F2EF",
                ["PanelBrush"] = "#FC12161A",
                ["CardBrush"] = "#B81A1E23",
                ["BorderBrush"] = "#4C565E",
                ["MutedBrush"] = "#8D969D",
                ["ControlBackgroundBrush"] = "#E0161B20",
                ["ControlHoverBrush"] = "#2A30363B",
                ["AccentBrush"] = "#FF73E3AC",
                ["AccentSoftBrush"] = "#2673E3AC",
                ["HeaderBackgroundBrush"] = "#80171B20",
                ["SeparatorBrush"] = "#292F34",
                ["NeutralButtonBrush"] = "#252B30",
                ["SaveButtonBrush"] = "#FF73E3AC",
                ["SaveTextBrush"] = "#FF102019",
                ["ToolTipBackgroundBrush"] = "#FC171B20",
                ["ToolTipBorderBrush"] = "#4D565E",
            };

        foreach (var (key, color) in colors)
        {
            Resources[key] = new SolidColorBrush(
                (System.Windows.Media.Color)
                    System.Windows.Media.ColorConverter.ConvertFromString(color)!);
        }

        if (SystemParameters.HighContrast)
        {
            Resources["PrimaryBrush"] = SystemColors.WindowTextBrush;
            Resources["PanelBrush"] = SystemColors.WindowBrush;
            Resources["CardBrush"] = SystemColors.WindowBrush;
            Resources["BorderBrush"] = SystemColors.WindowTextBrush;
            Resources["MutedBrush"] = SystemColors.GrayTextBrush;
            Resources["ControlBackgroundBrush"] = SystemColors.WindowBrush;
            Resources["ControlHoverBrush"] = SystemColors.ControlBrush;
            Resources["AccentBrush"] = SystemColors.HighlightBrush;
            Resources["AccentSoftBrush"] = SystemColors.ControlBrush;
            Resources["HeaderBackgroundBrush"] = SystemColors.WindowBrush;
            Resources["SeparatorBrush"] = SystemColors.WindowTextBrush;
            Resources["NeutralButtonBrush"] = SystemColors.ControlBrush;
            Resources["SaveButtonBrush"] = SystemColors.HighlightBrush;
            Resources["SaveTextBrush"] = SystemColors.HighlightTextBrush;
            Resources["ToolTipBackgroundBrush"] = SystemColors.WindowBrush;
            Resources["ToolTipBorderBrush"] = SystemColors.WindowTextBrush;
        }
    }

    private AppThemeMode SelectedTheme =>
        ThemeService.ParseMode(
            (ThemeComboBox.SelectedItem as ComboBoxItem)?.Tag as string);

    private AppLanguageMode SelectedLanguage =>
        AppLanguagePolicy.ParseMode(
            (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag as string);

    private CollapsedWidgetMode SelectedCollapsedMode =>
        CollapsedWidgetModePolicy.Parse(
            (CollapsedModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string);

    private void SelectTheme(AppThemeMode themeMode)
    {
        foreach (var item in ThemeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (ThemeService.ParseMode(item.Tag as string) == themeMode)
            {
                ThemeComboBox.SelectedItem = item;
                return;
            }
        }

        ThemeComboBox.SelectedIndex = 0;
    }

    private void SelectLanguage(AppLanguageMode languageMode)
    {
        foreach (var item in LanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            if (AppLanguagePolicy.ParseMode(item.Tag as string) == languageMode)
            {
                LanguageComboBox.SelectedItem = item;
                return;
            }
        }

        LanguageComboBox.SelectedIndex = 0;
    }

    private void SelectCollapsedMode(CollapsedWidgetMode collapsedMode)
    {
        foreach (var item in CollapsedModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (CollapsedWidgetModePolicy.Parse(item.Tag as string) ==
                collapsedMode)
            {
                CollapsedModeComboBox.SelectedItem = item;
                return;
            }
        }

        CollapsedModeComboBox.SelectedIndex = 0;
    }

    private void SettingsWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (systemEventsSubscribed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged += SystemEvents_OnUserPreferenceChanged;
        systemEventsSubscribed = true;
    }

    private void SettingsWindow_OnClosed(object? sender, EventArgs e)
    {
        if (!systemEventsSubscribed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= SystemEvents_OnUserPreferenceChanged;
        systemEventsSubscribed = false;
    }

    private void SystemEvents_OnUserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                ApplyTheme(ThemeService.ShouldUseLightTheme(SelectedTheme));
            });
    }

    private void ThemeComboBox_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (initializing)
        {
            return;
        }

        ApplyTheme(ThemeService.ShouldUseLightTheme(SelectedTheme));
    }

    private void GlassTransparencySlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (initializing)
        {
            return;
        }

        GlassTransparencyPreviewChanged?.Invoke(
            GlassTransparencyPolicy.Normalize(
                (int)Math.Round(
                    e.NewValue,
                    MidpointRounding.AwayFromZero)));
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Browse_OnClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = LocalizationService.Instance.Get(
                "Loc.SelectHomeDescription"),
            InitialDirectory = Directory.Exists(CodexHomeTextBox.Text)
                ? CodexHomeTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            CodexHomeTextBox.Text = dialog.SelectedPath;
        }
    }

    private void RebuildIndex_OnClick(object sender, RoutedEventArgs e)
    {
        RebuildIndexRequested = true;
        System.Windows.MessageBox.Show(
            this,
            LocalizationService.Instance.Get("Loc.RebuildIndexMessage"),
            LocalizationService.Instance.Get("Loc.RebuildIndex"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var homePath = CodexHomeTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(homePath)
            || !Directory.Exists(homePath)
            || (!Directory.Exists(Path.Combine(homePath, "sessions"))
                && !Directory.Exists(Path.Combine(homePath, "archived_sessions"))
                && !File.Exists(Path.Combine(homePath, "session_index.jsonl"))))
        {
            System.Windows.MessageBox.Show(
                this,
                LocalizationService.Instance.Get("Loc.InvalidHomeMessage"),
                LocalizationService.Instance.Get("Loc.InvalidPath"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ResultSettings = new AppSettings
        {
            CodexHomePath = homePath,
            AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true,
            AutoCollapse = AutoCollapseCheckBox.IsChecked == true,
            // Retain the obsolete value solely for old settings compatibility.
            AutoCollapseDelayMs = originalSettings.AutoCollapseDelayMs,
            StartWithWindows = StartupCheckBox.IsChecked == true,
            PauseMonitoringWhenDisplayOff =
                PauseMonitoringWhenDisplayOffCheckBox.IsChecked == true,
            ThemeMode = SelectedTheme.ToString(),
            LanguageMode = AppLanguagePolicy.ToSettingValue(
                SelectedLanguage),
            CollapsedMode = CollapsedWidgetModePolicy.ToSettingValue(
                SelectedCollapsedMode),
            GlassTransparencyPercent = GlassTransparencyPolicy.Normalize(
                (int)Math.Round(
                    GlassTransparencySlider.Value,
                    MidpointRounding.AwayFromZero)),
            WindowLeft = originalSettings.WindowLeft,
            WindowTop = originalSettings.WindowTop,
            IsPinned = originalSettings.IsPinned,
            SelectedPeriod = originalSettings.SelectedPeriod,
        }.Normalize();

        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

}
