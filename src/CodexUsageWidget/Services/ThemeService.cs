using System.IO;
using Microsoft.Win32;

namespace CodexUsageWidget.Services;

public static class ThemeService
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static AppThemeMode ParseMode(string? themeMode) =>
        Enum.TryParse<AppThemeMode>(themeMode, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : AppThemeMode.System;

    public static bool ShouldUseLightTheme(string? themeMode) =>
        ShouldUseLightTheme(ParseMode(themeMode));

    public static bool ShouldUseLightTheme(AppThemeMode themeMode) =>
        themeMode switch
        {
            AppThemeMode.Light => true,
            AppThemeMode.Dark => false,
            _ => IsSystemLightTheme(),
        };

    public static bool IsSystemLightTheme()
    {
        return ReadPersonalizationFlag(
            "AppsUseLightTheme",
            fallback: false);
    }

    public static bool IsSystemShellLightTheme(bool fallback)
    {
        return ReadPersonalizationFlag(
            "SystemUsesLightTheme",
            fallback);
    }

    private static bool ReadPersonalizationFlag(
        string valueName,
        bool fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                PersonalizeKey,
                writable: false);
            return key?.GetValue(valueName) switch
            {
                int value => value != 0,
                long value => value != 0,
                _ => fallback,
            };
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or System.Security.SecurityException
                or IOException)
        {
            return fallback;
        }
    }
}
