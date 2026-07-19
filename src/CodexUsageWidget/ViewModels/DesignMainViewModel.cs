namespace CodexUsageWidget.ViewModels;

/// <summary>
/// Visual Studio/Blend-only sample values. Runtime data is supplied by the dashboard service.
/// </summary>
public sealed class DesignMainViewModel : MainViewModel
{
    public DesignMainViewModel()
    {
        RemainingPercent = 85;
        SetResetTextLiteral("6天12小时后重置");
        SetRateLimitSummaryLiteral("主要窗口：剩余 85%，6天12小时后重置");
        WeeklyQuotaUsedPercent = 46.2;
        SetWeeklyQuotaStatusLiteral("日志直接观测 · 更新 03:44");
        IsLive = true;
        IsExpanded = true;

        var todayDate = DateOnly.FromDateTime(DateTime.Today);
        var dailyChange = new double?[] { null, 7.8, 5.1, 11.6, 4.2, 8.7, 5.4 };
        var weeklyClosing = new[] { 3.4, 11.2, 16.3, 27.9, 32.1, 40.8, 46.2 };
        ReplaceWeeklyQuotaDays(
            Enumerable.Range(0, 7)
                .Select(index => new WeeklyQuotaDayViewModel
                {
                    Date = todayDate.AddDays(index - 6),
                    IsToday = index == 6,
                    ChangeFromPreviousDayPercent = dailyChange[index],
                    ClosingUsedPercent = weeklyClosing[index],
                    LastObservedAt = DateTimeOffset.Now.AddDays(index - 6),
                    SampleCount = 12 + index,
                    IsObserved = true,
                    IsPartial = index is 0 or 6,
                    BarHeight = 6 + (32 * weeklyClosing[index] / 100d),
                }));

        ReplaceRateLimitWindows(
        [
            new RateLimitWindowViewModel
            {
                Name = "主要窗口",
                RemainingPercent = 85,
                WindowMinutes = 10080,
                DisplayText = "剩余 85% · 6天12小时后重置",
            },
        ]);

        var rows = new[]
        {
            ("代码审查", 10_141_737_988L),
            ("数据分析", 6_105_086_420L),
            ("文档生成", 4_184_405_132L),
            ("测试自动化", 602_824_973L),
            ("性能优化", 602_629_969L),
            ("界面重构", 310_201_648L),
            ("本地化", 217_036_651L),
            ("图标设计", 81_173_934L),
            ("发布准备", 66_949_488L),
            ("其他", 125_011_563L),
        };

        var maximum = rows[0].Item2;
        var rankingRows = rows.Select((row, index) => new TaskUsageRowViewModel
        {
            Rank = index + 1,
            Title = row.Item1,
            TotalTokens = row.Item2,
            InputTokens = (long)(row.Item2 * 0.994),
            CachedInputTokens = (long)(row.Item2 * 0.96),
            OutputTokens = (long)(row.Item2 * 0.006),
            ReasoningOutputTokens = (long)(row.Item2 * 0.002),
            Share = (double)row.Item2 / maximum,
            PeriodShare = (double)row.Item2 / 22_647_091_027L,
            IsArchived = index == 7,
            IsAggregate = index == 9,
        });

        var today = GetPeriod(UsagePeriodKind.Today);
        today.ReplaceRankings(rankingRows);
        today.Summary = new UsageSummaryViewModel
        {
            TotalTokens = 22_647_091_027,
            InputTokens = 22_579_030_454,
            OutputTokens = 68_060_573,
            CachedInputTokens = 21_838_359_936,
            ReasoningOutputTokens = 24_261_492,
            TaskCount = 41,
            TopNineShare = 0.9945,
            OtherTokens = 125_011_563,
            LastRefresh = DateTimeOffset.Now,
        };

        foreach (var period in Periods.Where(period => period.Kind != UsagePeriodKind.Today))
        {
            period.ReplaceRankings(today.Rankings.Select(row => new TaskUsageRowViewModel
            {
                Rank = row.Rank,
                Title = row.Title,
                TotalTokens = row.TotalTokens,
                InputTokens = row.InputTokens,
                CachedInputTokens = row.CachedInputTokens,
                OutputTokens = row.OutputTokens,
                ReasoningOutputTokens = row.ReasoningOutputTokens,
                Share = row.Share,
                PeriodShare = row.PeriodShare,
                IsArchived = row.IsArchived,
                IsAggregate = row.IsAggregate,
            }));
            period.Summary = today.Summary;
        }
    }
}
