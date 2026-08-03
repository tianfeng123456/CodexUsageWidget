using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CodexUsageWidget.Core;

namespace CodexUsageWidget.Services;

/// <summary>
/// Loads one small, embedded UI string dictionary. Language changes only
/// re-project text already held in memory; this service never touches logs,
/// SQLite, the file watcher, or the dashboard refresh pipeline.
/// </summary>
public sealed class LocalizationService
{
    private const string EnglishResourcePath =
        "/CodexUsageWidget;component/Resources/Strings.en.xaml";
    private const string ChineseResourcePath =
        "/CodexUsageWidget;component/Resources/Strings.zh-Hans.xaml";

    private readonly CultureInfo systemUiCulture =
        CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentUICulture.Clone());
    private ReadOnlyDictionary<string, string> strings =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal));
    private ResourceDictionary? activeLanguageDictionary;

    private LocalizationService()
    {
    }

    public static LocalizationService Instance { get; } = new();

    public AppLanguageMode Mode { get; private set; } = AppLanguageMode.System;

    public CultureInfo Culture { get; private set; } =
        CultureInfo.GetCultureInfo("en-US");

    public string EffectiveLanguageTag => Culture.Name;

    public event EventHandler? LanguageChanged;

    public bool Apply(string? configuredMode) =>
        Apply(AppLanguagePolicy.ParseMode(configuredMode));

    public bool Apply(AppLanguageMode mode)
    {
        if (System.Windows.Application.Current is null)
        {
            throw new InvalidOperationException(
                "Localization requires an active WPF application.");
        }

        var nextCulture = AppLanguagePolicy.ResolveCulture(
            mode,
            systemUiCulture.Name);
        var effectiveChanged = !string.Equals(
            Culture.Name,
            nextCulture.Name,
            StringComparison.OrdinalIgnoreCase);
        Mode = mode;

        if (effectiveChanged || strings.Count == 0)
        {
            ReplaceLanguageDictionary(nextCulture);
            Culture = nextCulture;
            CultureInfo.CurrentUICulture = nextCulture;
            CultureInfo.DefaultThreadCurrentUICulture = nextCulture;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        return effectiveChanged;
    }

    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return strings.TryGetValue(key, out var value)
            ? value
            : $"[{key}]";
    }

    public string Format(string key, params object?[] args) =>
        string.Format(Culture, Get(key), args);

    public string FormatDate(DateOnly date, string patternKey) =>
        date.ToDateTime(TimeOnly.MinValue).ToString(
            Get(patternKey),
            Culture);

    public string FormatDateTime(DateTimeOffset timestamp, string patternKey) =>
        timestamp.ToLocalTime().ToString(
            Get(patternKey),
            Culture);

    private void ReplaceLanguageDictionary(CultureInfo culture)
    {
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (activeLanguageDictionary is not null)
        {
            merged.Remove(activeLanguageDictionary);
            activeLanguageDictionary = null;
        }

        if (culture.TwoLetterISOLanguageName.Equals(
                "zh",
                StringComparison.OrdinalIgnoreCase))
        {
            activeLanguageDictionary = new ResourceDictionary
            {
                Source = new Uri(ChineseResourcePath, UriKind.Relative),
            };
            merged.Add(activeLanguageDictionary);
        }

        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dictionary in merged)
        {
            CopyStrings(dictionary, snapshot);
        }

        // Be defensive if App.xaml is ever reorganized: English remains the
        // exhaustive fallback even when its merged dictionary is missing.
        if (snapshot.Count == 0)
        {
            var fallback = new ResourceDictionary
            {
                Source = new Uri(EnglishResourcePath, UriKind.Relative),
            };
            CopyStrings(fallback, snapshot);
        }

        strings = new ReadOnlyDictionary<string, string>(snapshot);
    }

    private static void CopyStrings(
        ResourceDictionary source,
        IDictionary<string, string> destination)
    {
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                destination[key] = value;
            }
        }
    }
}
