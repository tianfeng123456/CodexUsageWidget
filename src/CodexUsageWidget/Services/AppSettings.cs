using CodexUsageWidget.Core;

namespace CodexUsageWidget.Services;

public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

public enum CollapsedWidgetMode
{
    Circle,
    Capsule,
}

public static class CollapsedWidgetModePolicy
{
    public static CollapsedWidgetMode Parse(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "capsule" => CollapsedWidgetMode.Capsule,
            _ => CollapsedWidgetMode.Circle,
        };
    }

    public static string ToSettingValue(CollapsedWidgetMode mode) =>
        mode == CollapsedWidgetMode.Capsule
            ? nameof(CollapsedWidgetMode.Capsule)
            : nameof(CollapsedWidgetMode.Circle);
}

public sealed class AppSettings
{
    public string CodexHomePath { get; set; } = string.Empty;

    public bool AlwaysOnTop { get; set; } = true;

    public bool AutoCollapse { get; set; } = true;

    public int AutoCollapseDelayMs { get; set; } = 800;

    public bool StartWithWindows { get; set; }

    public bool PauseMonitoringWhenDisplayOff { get; set; } = true;

    public string ThemeMode { get; set; } = nameof(AppThemeMode.System);

    public string LanguageMode { get; set; } = nameof(AppLanguageMode.System);

    public string CollapsedMode { get; set; } =
        nameof(CollapsedWidgetMode.Circle);

    public int GlassTransparencyPercent { get; set; } =
        GlassTransparencyPolicy.DefaultPercent;

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public bool IsPinned { get; set; }

    public string SelectedPeriod { get; set; } = "Today";

    public AppSettings Normalize()
    {
        // Kept only so settings written by older releases can still be read.
        // Auto-collapse is now immediate and this value is intentionally ignored.
        AutoCollapseDelayMs = Math.Clamp(AutoCollapseDelayMs, 250, 5000);
        ThemeMode = ThemeService.ParseMode(ThemeMode).ToString();
        LanguageMode = AppLanguagePolicy.ToSettingValue(
            AppLanguagePolicy.ParseMode(LanguageMode));
        CollapsedMode = CollapsedWidgetModePolicy.ToSettingValue(
            CollapsedWidgetModePolicy.Parse(CollapsedMode));
        GlassTransparencyPercent = GlassTransparencyPolicy.Normalize(
            GlassTransparencyPercent);
        SelectedPeriod = SelectedPeriod is "Today" or "Last7Days" or "ThisMonth" or "AllTime"
            ? SelectedPeriod
            : "Today";
        return this;
    }
}
