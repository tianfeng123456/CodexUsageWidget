using System.Collections.ObjectModel;
using CodexUsageWidget.Services;

namespace CodexUsageWidget.ViewModels;

public enum UsagePeriodKind
{
    Today,
    LastSevenDays,
    CurrentMonth,
    AllTime,
}

public sealed class RateLimitWindowViewModel
{
    public string Name { get; init; } = string.Empty;

    public double? RemainingPercent { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }

    public int? WindowMinutes { get; init; }

    public string DisplayText { get; init; } = string.Empty;
}

public sealed class WeeklyQuotaDayViewModel : ObservableObject
{
    public DateOnly Date { get; init; }

    public bool IsToday { get; init; }

    public string DateLabel =>
        IsToday
            ? LocalizationService.Instance.Get("Loc.Today")
            : Date.ToString("M/d", LocalizationService.Instance.Culture);

    public string LongDateLabel =>
        LocalizationService.Instance.FormatDate(
            Date,
            "Loc.DateLongPattern");

    public double? ChangeFromPreviousDayPercent { get; init; }

    public double? DailyConsumedPercent { get; init; }

    public double? ClosingUsedPercent { get; init; }

    public DateTimeOffset? LastObservedAt { get; init; }

    public int SampleCount { get; init; }

    public bool IsObserved { get; init; }

    public bool IsPartial { get; init; }

    /// <summary>
    /// Render height for the compact daily bar, in device-independent pixels.
    /// It is calculated only when fresh data is applied.
    /// </summary>
    public double BarHeight { get; init; }

    public string UsedDisplay =>
        DailyConsumedPercent is { } consumed
            ? (IsPartial ? "≥" : string.Empty) +
              consumed.ToString(
                  "0.#",
                  LocalizationService.Instance.Culture) + "%"
            : "—";

    public string DailyConsumedDisplay =>
        DailyConsumedPercent is { } consumed
            ? LocalizationService.Instance.Format(
                IsPartial
                    ? "Loc.DailyConsumedMinimumFormat"
                    : "Loc.DailyConsumedFormat",
                consumed)
            : LocalizationService.Instance.Get("Loc.DailyConsumedUnknown");

    public string ChangeDisplay =>
        ChangeFromPreviousDayPercent is { } change
            ? LocalizationService.Instance.Format(
                "Loc.PreviousDayChangeFormat",
                change)
            : LocalizationService.Instance.Get("Loc.PreviousDayUnknown");

    public string ClosingDisplay =>
        ClosingUsedPercent is { } closing
            ? LocalizationService.Instance.Format(
                "Loc.WeeklyUsedOnlyFormat",
                closing)
            : LocalizationService.Instance.Get("Loc.WeeklyUsageUnknown");

    public string LastObservedDisplay =>
        LastObservedAt is { } timestamp
            ? LocalizationService.Instance.Format(
                "Loc.LastObservedFormat",
                timestamp.ToLocalTime())
            : LocalizationService.Instance.Get("Loc.NoObservationToday");

    public string ToolTipText =>
        IsObserved
            ? LocalizationService.Instance.Format(
                "Loc.WeeklyTooltipObservedFormat",
                LongDateLabel,
                DailyConsumedDisplay,
                ChangeDisplay,
                ClosingDisplay,
                LastObservedDisplay)
            : LocalizationService.Instance.Format(
                "Loc.WeeklyTooltipMissingFormat",
                LongDateLabel);

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(LongDateLabel));
        OnPropertyChanged(nameof(UsedDisplay));
        OnPropertyChanged(nameof(DailyConsumedDisplay));
        OnPropertyChanged(nameof(ChangeDisplay));
        OnPropertyChanged(nameof(ClosingDisplay));
        OnPropertyChanged(nameof(LastObservedDisplay));
        OnPropertyChanged(nameof(ToolTipText));
    }
}

public sealed class TaskUsageRowViewModel : ObservableObject
{
    private string _title = string.Empty;

    public int Rank { get; init; }

    public string Title
    {
        get
        {
            if (IsAggregate)
            {
                return LocalizationService.Instance.Get("Loc.Other");
            }

            var suffix = TryGetUntitledSuffix(_title);
            return suffix is null
                ? _title
                : LocalizationService.Instance.Format(
                    "Loc.UntitledTaskFormat",
                    suffix);
        }
        init => _title = value ?? string.Empty;
    }

    public long TotalTokens { get; init; }

    public long InputTokens { get; init; }

    public long CachedInputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long ReasoningOutputTokens { get; init; }

    /// <summary>
    /// Fraction in the inclusive range 0..1 relative to the largest item in the active period.
    /// </summary>
    public double Share { get; init; }

    /// <summary>
    /// Fraction in the inclusive range 0..1 of the entire active period.
    /// </summary>
    public double PeriodShare { get; init; }

    public bool IsArchived { get; init; }

    /// <summary>
    /// True for the synthetic tenth row that aggregates every task outside
    /// the first nine. Aggregate rows intentionally have no task details.
    /// </summary>
    public bool IsAggregate { get; init; }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Title));
        // Re-run value converters whose output depends on the active culture.
        OnPropertyChanged(nameof(TotalTokens));
        OnPropertyChanged(nameof(InputTokens));
        OnPropertyChanged(nameof(CachedInputTokens));
        OnPropertyChanged(nameof(OutputTokens));
        OnPropertyChanged(nameof(ReasoningOutputTokens));
        OnPropertyChanged(nameof(PeriodShare));
    }

    private static string? TryGetUntitledSuffix(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        const string chinesePrefix = "未命名任务 ";
        const string englishPrefix = "Untitled task ";
        if (title.StartsWith(chinesePrefix, StringComparison.Ordinal))
        {
            return title[chinesePrefix.Length..];
        }

        return title.StartsWith(englishPrefix, StringComparison.OrdinalIgnoreCase)
            ? title[englishPrefix.Length..]
            : null;
    }
}

public sealed class UsageSummaryViewModel : ObservableObject
{
    private long _totalTokens;
    private long _inputTokens;
    private long _outputTokens;
    private long _cachedInputTokens;
    private long _reasoningOutputTokens;
    private int _taskCount;
    private double _topNineShare;
    private long _otherTokens;
    private DateTimeOffset? _lastRefresh;

    public long TotalTokens
    {
        get => _totalTokens;
        set => SetProperty(ref _totalTokens, value);
    }

    public long InputTokens
    {
        get => _inputTokens;
        set => SetProperty(ref _inputTokens, value);
    }

    public long OutputTokens
    {
        get => _outputTokens;
        set => SetProperty(ref _outputTokens, value);
    }

    public long CachedInputTokens
    {
        get => _cachedInputTokens;
        set => SetProperty(ref _cachedInputTokens, value);
    }

    public long ReasoningOutputTokens
    {
        get => _reasoningOutputTokens;
        set => SetProperty(ref _reasoningOutputTokens, value);
    }

    public int TaskCount
    {
        get => _taskCount;
        set => SetProperty(ref _taskCount, value);
    }

    public double TopNineShare
    {
        get => _topNineShare;
        set => SetProperty(ref _topNineShare, Math.Clamp(value, 0, 1));
    }

    public long OtherTokens
    {
        get => _otherTokens;
        set => SetProperty(ref _otherTokens, value);
    }

    public DateTimeOffset? LastRefresh
    {
        get => _lastRefresh;
        set
        {
            if (SetProperty(ref _lastRefresh, value))
            {
                OnPropertyChanged(nameof(LastRefreshDisplay));
            }
        }
    }

    public string LastRefreshDisplay =>
        LastRefresh is { } timestamp
            ? LocalizationService.Instance.Format(
                "Loc.LastRefreshFormat",
                timestamp.ToLocalTime())
            : LocalizationService.Instance.Get("Loc.NeverRefreshed");

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(TotalTokens));
        OnPropertyChanged(nameof(InputTokens));
        OnPropertyChanged(nameof(OutputTokens));
        OnPropertyChanged(nameof(CachedInputTokens));
        OnPropertyChanged(nameof(ReasoningOutputTokens));
        OnPropertyChanged(nameof(TopNineShare));
        OnPropertyChanged(nameof(OtherTokens));
        OnPropertyChanged(nameof(LastRefreshDisplay));
    }
}

public sealed class UsagePeriodViewModel : ObservableObject
{
    private UsageSummaryViewModel _summary = new();
    private bool _isLoading;
    private bool _isLoaded;

    public UsagePeriodViewModel(UsagePeriodKind kind)
    {
        Kind = kind;
    }

    public UsagePeriodKind Kind { get; }

    public string DisplayName =>
        LocalizationService.Instance.Get(
            Kind switch
            {
                UsagePeriodKind.Today => "Loc.PeriodToday",
                UsagePeriodKind.LastSevenDays => "Loc.PeriodSevenDays",
                UsagePeriodKind.CurrentMonth => "Loc.PeriodMonth",
                _ => "Loc.PeriodAll",
            });

    public string SectionTitle =>
        LocalizationService.Instance.Format(
            "Loc.PeriodSectionTitleFormat",
            DisplayName);

    public ObservableCollection<TaskUsageRowViewModel> Rankings { get; } = [];

    public UsageSummaryViewModel Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value ?? new UsageSummaryViewModel());
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        set => SetProperty(ref _isLoaded, value);
    }

    public void ReplaceRankings(IEnumerable<TaskUsageRowViewModel> rows)
    {
        Rankings.Clear();
        foreach (var row in rows.Take(10))
        {
            Rankings.Add(row);
        }
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SectionTitle));
        Summary.RefreshLocalization();
        foreach (var row in Rankings)
        {
            row.RefreshLocalization();
        }
    }
}
