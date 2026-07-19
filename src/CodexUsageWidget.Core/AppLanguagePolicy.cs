using System.Globalization;

namespace CodexUsageWidget.Core;

/// <summary>
/// User-selectable application language modes.
/// </summary>
public enum AppLanguageMode
{
    System,
    ZhHans,
    English,
}

/// <summary>
/// Pure policy helpers for parsing a saved language mode and resolving it to
/// one of the cultures supported by the application.
/// </summary>
public static class AppLanguagePolicy
{
    public const string SimplifiedChineseCultureName = "zh-CN";
    public const string EnglishCultureName = "en-US";

    /// <summary>
    /// Parses the persisted language value. Unknown values deliberately fall
    /// back to <see cref="AppLanguageMode.System"/> for forward compatibility.
    /// </summary>
    public static AppLanguageMode ParseMode(string? configuredMode)
    {
        return configuredMode?.Trim().ToLowerInvariant() switch
        {
            "system" => AppLanguageMode.System,
            "zh-hans" or "zhhans" => AppLanguageMode.ZhHans,
            "en" or "english" => AppLanguageMode.English,
            _ => AppLanguageMode.System,
        };
    }

    /// <summary>
    /// Returns a valid mode even when an out-of-range enum value is supplied.
    /// </summary>
    public static AppLanguageMode NormalizeMode(AppLanguageMode mode)
    {
        return Enum.IsDefined(typeof(AppLanguageMode), mode)
            ? mode
            : AppLanguageMode.System;
    }

    /// <summary>
    /// Returns the stable value written to settings.
    /// </summary>
    public static string ToSettingValue(AppLanguageMode mode)
    {
        return NormalizeMode(mode) switch
        {
            AppLanguageMode.ZhHans => "zh-Hans",
            AppLanguageMode.English => "en",
            _ => "System",
        };
    }

    /// <summary>
    /// Resolves a saved setting and the current system UI culture to a
    /// supported concrete culture name.
    /// </summary>
    public static string ResolveCultureName(
        string? configuredMode,
        string? systemCultureName)
    {
        return ResolveCultureName(
            ParseMode(configuredMode),
            systemCultureName);
    }

    /// <summary>
    /// Resolves a language mode and the current system UI culture to a
    /// supported concrete culture name. Explicit modes always win. System mode
    /// maps every Chinese culture to zh-CN and every other culture to en-US.
    /// </summary>
    public static string ResolveCultureName(
        AppLanguageMode mode,
        string? systemCultureName)
    {
        return NormalizeMode(mode) switch
        {
            AppLanguageMode.ZhHans => SimplifiedChineseCultureName,
            AppLanguageMode.English => EnglishCultureName,
            _ => IsChineseCultureName(systemCultureName)
                ? SimplifiedChineseCultureName
                : EnglishCultureName,
        };
    }

    public static CultureInfo ResolveCulture(
        AppLanguageMode mode,
        string? systemCultureName)
    {
        return CultureInfo.GetCultureInfo(
            ResolveCultureName(mode, systemCultureName));
    }

    public static CultureInfo ResolveCulture(
        string? configuredMode,
        string? systemCultureName)
    {
        return CultureInfo.GetCultureInfo(
            ResolveCultureName(configuredMode, systemCultureName));
    }

    /// <summary>
    /// Returns true for the neutral Chinese culture and every zh-* culture.
    /// </summary>
    public static bool IsChineseCultureName(string? cultureName)
    {
        var normalized = cultureName?.Trim();
        return string.Equals(
                   normalized,
                   "zh",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized?.StartsWith(
                   "zh-",
                   StringComparison.OrdinalIgnoreCase) == true;
    }
}
