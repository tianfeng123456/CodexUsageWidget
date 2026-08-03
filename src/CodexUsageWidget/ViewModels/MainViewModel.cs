using System.Collections.ObjectModel;
using System.Windows.Input;
using CodexUsageWidget.Core;
using CodexUsageWidget.Services;

namespace CodexUsageWidget.ViewModels;

public class MainViewModel : ObservableObject
{
    private double? _remainingPercent;
    private LocalizedMessage _resetText =
        new("Loc.Unavailable", []);
    private LocalizedMessage _rateLimitSummary =
        new("Loc.RateLimitUnavailable", []);
    private RemainingQuotaStatus _capsuleQuotaStatus =
        RemainingQuotaStatus.WaitingForData;
    private bool _isLive;
    private bool _isExpanded;
    private bool _isPinned;
    private bool _isAlwaysOnTop = true;
    private bool _autoCollapseEnabled = true;
    private int _autoCollapseDelayMilliseconds = 800;
    private bool _isRefreshing;
    private bool _isIndexing;
    private bool _isBuildingHistory;
    private double _indexProgress;
    private IndexProgressStage _indexProgressStage = IndexProgressStage.Idle;
    private double? _weeklyQuotaUsedPercent;
    private bool _isWeeklyQuotaOverlayOpen;
    private bool _isWeeklyQuotaLoading;
    private LocalizedMessage _weeklyQuotaStatusText =
        new("Loc.ClickWeeklyObservations", []);
    private WeeklyQuotaDayViewModel? _hoveredWeeklyQuotaDay;
    private UsagePeriodViewModel? _selectedPeriod;

    public MainViewModel()
    {
        Periods =
        [
            new UsagePeriodViewModel(UsagePeriodKind.Today),
            new UsagePeriodViewModel(UsagePeriodKind.LastSevenDays),
            new UsagePeriodViewModel(UsagePeriodKind.CurrentMonth),
            new UsagePeriodViewModel(UsagePeriodKind.AllTime),
        ];
        _selectedPeriod = Periods[0];

        TogglePinCommand = new RelayCommand(_ => IsPinned = !IsPinned);
        RefreshCommand = new RelayCommand(_ => RefreshRequested?.Invoke(this, EventArgs.Empty));
        OpenSettingsCommand = new RelayCommand(_ => SettingsRequested?.Invoke(this, EventArgs.Empty));
        ToggleWeeklyQuotaCommand = new RelayCommand(
            _ =>
            {
                IsWeeklyQuotaOverlayOpen = !IsWeeklyQuotaOverlayOpen;
                if (IsWeeklyQuotaOverlayOpen)
                {
                    WeeklyQuotaHistoryRequested?.Invoke(this, EventArgs.Empty);
                }
            });
        CloseWeeklyQuotaCommand = new RelayCommand(_ => IsWeeklyQuotaOverlayOpen = false);
    }

    public event EventHandler? RefreshRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? WeeklyQuotaHistoryRequested;

    public ObservableCollection<UsagePeriodViewModel> Periods { get; }

    public ObservableCollection<RateLimitWindowViewModel> RateLimitWindows { get; } = [];

    public ObservableCollection<WeeklyQuotaDayViewModel> WeeklyQuotaDays { get; } = [];

    public ICommand TogglePinCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand ToggleWeeklyQuotaCommand { get; }

    public ICommand CloseWeeklyQuotaCommand { get; }

    public double? RemainingPercent
    {
        get => _remainingPercent;
        set
        {
            double? normalized = value is { } number && double.IsFinite(number)
                ? Math.Clamp(number, 0, 100)
                : null;
            if (SetProperty(ref _remainingPercent, normalized))
            {
                OnPropertyChanged(nameof(RemainingDisplay));
                OnPropertyChanged(nameof(RemainingAutomationName));
                NotifyCapsuleQuotaStatusChanged();
            }
        }
    }

    public string RemainingDisplay =>
        RemainingPercent is { } remaining
            ? $"{RemainingQuotaStatusPolicy.ToDisplayPercentage(remaining)}%"
            : "--";

    public string RemainingAutomationName =>
        LocalizationService.Instance.Format(
            "Loc.RemainingAutomationFormat",
            RemainingDisplay);

    public RemainingQuotaStatus CapsuleQuotaStatus => _capsuleQuotaStatus;

    public string CapsuleQuotaStatusText =>
        LocalizationService.Instance.Get(
            CapsuleQuotaStatus switch
            {
                RemainingQuotaStatus.Syncing => "Loc.QuotaStatusSyncing",
                RemainingQuotaStatus.Sufficient => "Loc.QuotaStatusSufficient",
                RemainingQuotaStatus.Stable => "Loc.UsageStable",
                RemainingQuotaStatus.Low => "Loc.QuotaStatusLow",
                RemainingQuotaStatus.NearlyExhausted =>
                    "Loc.QuotaStatusNearlyExhausted",
                RemainingQuotaStatus.Exhausted => "Loc.QuotaStatusExhausted",
                _ => "Loc.QuotaStatusWaitingForData",
            });

    public string ResetText => _resetText.Resolve();

    public string RateLimitSummary => _rateLimitSummary.Resolve();

    public double? WeeklyQuotaUsedPercent
    {
        get => _weeklyQuotaUsedPercent;
        set
        {
            double? normalized = value is { } number && double.IsFinite(number)
                ? Math.Clamp(number, 0, 100)
                : null;
            if (SetProperty(ref _weeklyQuotaUsedPercent, normalized))
            {
                OnPropertyChanged(nameof(WeeklyQuotaUsedDisplay));
            }
        }
    }

    public string WeeklyQuotaUsedDisplay =>
        WeeklyQuotaUsedPercent is { } used
            ? LocalizationService.Instance.Format(
                "Loc.WeeklyUsedFormat",
                used)
            : LocalizationService.Instance.Get("Loc.WeeklyQuotaUnknown");

    public bool IsWeeklyQuotaOverlayOpen
    {
        get => _isWeeklyQuotaOverlayOpen;
        set => SetProperty(ref _isWeeklyQuotaOverlayOpen, value);
    }

    public bool IsWeeklyQuotaLoading
    {
        get => _isWeeklyQuotaLoading;
        set => SetProperty(ref _isWeeklyQuotaLoading, value);
    }

    public string WeeklyQuotaStatusText => _weeklyQuotaStatusText.Resolve();

    public WeeklyQuotaDayViewModel? HoveredWeeklyQuotaDay
    {
        get => _hoveredWeeklyQuotaDay;
        set => SetProperty(ref _hoveredWeeklyQuotaDay, value);
    }

    public bool HasWeeklyQuotaData =>
        WeeklyQuotaDays.Any(static day => day.IsObserved);

    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (SetProperty(ref _isLive, value))
            {
                NotifyCapsuleQuotaStatusChanged();
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value) && value)
            {
                IsExpanded = true;
            }
        }
    }

    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        set => SetProperty(ref _isAlwaysOnTop, value);
    }

    public bool AutoCollapseEnabled
    {
        get => _autoCollapseEnabled;
        set => SetProperty(ref _autoCollapseEnabled, value);
    }

    public int AutoCollapseDelayMilliseconds
    {
        get => _autoCollapseDelayMilliseconds;
        set => SetProperty(ref _autoCollapseDelayMilliseconds, Math.Clamp(value, 100, 5000));
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                NotifyCapsuleQuotaStatusChanged();
            }
        }
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        set => SetProperty(ref _isIndexing, value);
    }

    public bool IsBuildingHistory
    {
        get => _isBuildingHistory;
        set
        {
            if (SetProperty(ref _isBuildingHistory, value))
            {
                NotifyIndexStatusChanged();
            }
        }
    }

    public double IndexProgress
    {
        get => _indexProgress;
        set
        {
            var normalized = double.IsFinite(value)
                ? Math.Clamp(value, 0, 1)
                : 0d;
            if (SetProperty(ref _indexProgress, normalized))
            {
                OnPropertyChanged(nameof(IndexStatusText));
            }
        }
    }

    public IndexProgressStage IndexProgressStage
    {
        get => _indexProgressStage;
        set
        {
            if (SetProperty(ref _indexProgressStage, value))
            {
                NotifyIndexStatusChanged();
            }
        }
    }

    public bool IsIndexingNoticeVisible =>
        IsBuildingHistory ||
        IndexProgressStage == IndexProgressStage.Incomplete;

    public bool IsIndexProgressVisible =>
        IsBuildingHistory &&
        IndexProgressStage is not IndexProgressStage.Idle;

    public string IndexStatusText =>
        IndexProgressStage switch
        {
            IndexProgressStage.Preparing =>
                LocalizationService.Instance.Get("Loc.PreparingHistoryIndex"),
            IndexProgressStage.Finalizing =>
                LocalizationService.Instance.Get("Loc.FinalizingHistoryIndex"),
            IndexProgressStage.Completed when IsBuildingHistory =>
                LocalizationService.Instance.Get("Loc.HistoryIndexComplete"),
            IndexProgressStage.Incomplete =>
                LocalizationService.Instance.Get("Loc.HistoryIndexIncomplete"),
            _ when IsBuildingHistory => LocalizationService.Instance.Format(
                "Loc.BuildingHistoryFormat",
                IndexProgress),
            _ => string.Empty,
        };

    public string IndexStatusDetailText =>
        IndexProgressStage == IndexProgressStage.Incomplete
            ? LocalizationService.Instance.Get("Loc.HistoryIndexIncompleteHint")
            : LocalizationService.Instance.Get("Loc.InitialIndexHint");

    public UsagePeriodViewModel? SelectedPeriod
    {
        get => _selectedPeriod;
        set => SetProperty(ref _selectedPeriod, value);
    }

    public UsagePeriodViewModel GetPeriod(UsagePeriodKind kind)
    {
        return Periods.First(period => period.Kind == kind);
    }

    public void ReplaceRateLimitWindows(IEnumerable<RateLimitWindowViewModel> windows)
    {
        RateLimitWindows.Clear();
        foreach (var window in windows)
        {
            RateLimitWindows.Add(window);
        }
    }

    public void ReplaceWeeklyQuotaDays(IEnumerable<WeeklyQuotaDayViewModel> days)
    {
        WeeklyQuotaDays.Clear();
        foreach (var day in days)
        {
            WeeklyQuotaDays.Add(day);
        }

        OnPropertyChanged(nameof(HasWeeklyQuotaData));
    }

    public void SetResetMessage(string key, params object?[] args)
    {
        _resetText = new LocalizedMessage(key, args);
        OnPropertyChanged(nameof(ResetText));
    }

    public void SetResetTextLiteral(string value)
    {
        _resetText = LocalizedMessage.Literal(value);
        OnPropertyChanged(nameof(ResetText));
    }

    public void SetRateLimitSummaryMessage(string key, params object?[] args)
    {
        _rateLimitSummary = new LocalizedMessage(key, args);
        OnPropertyChanged(nameof(RateLimitSummary));
    }

    public void SetRateLimitSummaryLiteral(string value)
    {
        _rateLimitSummary = LocalizedMessage.Literal(value);
        OnPropertyChanged(nameof(RateLimitSummary));
    }

    public void SetWeeklyQuotaStatusMessage(
        string key,
        params object?[] args)
    {
        _weeklyQuotaStatusText = new LocalizedMessage(key, args);
        OnPropertyChanged(nameof(WeeklyQuotaStatusText));
    }

    public void SetWeeklyQuotaStatusLiteral(string value)
    {
        _weeklyQuotaStatusText = LocalizedMessage.Literal(value);
        OnPropertyChanged(nameof(WeeklyQuotaStatusText));
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(RemainingAutomationName));
        OnPropertyChanged(nameof(CapsuleQuotaStatusText));
        OnPropertyChanged(nameof(ResetText));
        OnPropertyChanged(nameof(RateLimitSummary));
        OnPropertyChanged(nameof(WeeklyQuotaUsedDisplay));
        OnPropertyChanged(nameof(WeeklyQuotaStatusText));
        OnPropertyChanged(nameof(IndexStatusText));
        OnPropertyChanged(nameof(IndexStatusDetailText));

        foreach (var period in Periods)
        {
            period.RefreshLocalization();
        }

        foreach (var day in WeeklyQuotaDays)
        {
            day.RefreshLocalization();
        }
    }

    private void NotifyIndexStatusChanged()
    {
        OnPropertyChanged(nameof(IndexStatusText));
        OnPropertyChanged(nameof(IndexStatusDetailText));
        OnPropertyChanged(nameof(IsIndexingNoticeVisible));
        OnPropertyChanged(nameof(IsIndexProgressVisible));
    }

    private void NotifyCapsuleQuotaStatusChanged()
    {
        var next = RemainingQuotaStatusPolicy.Evaluate(
            RemainingPercent,
            IsRefreshing,
            IsLive);
        if (_capsuleQuotaStatus == next)
        {
            return;
        }

        _capsuleQuotaStatus = next;
        OnPropertyChanged(nameof(CapsuleQuotaStatus));
        OnPropertyChanged(nameof(CapsuleQuotaStatusText));
    }

    private sealed record LocalizedMessage(
        string? Key,
        object?[] Arguments,
        string? LiteralValue = null)
    {
        public static LocalizedMessage Literal(string value) =>
            new(null, [], value);

        public string Resolve() =>
            Key is null
                ? LiteralValue ?? string.Empty
                : LocalizationService.Instance.Format(Key, Arguments);
    }
}
