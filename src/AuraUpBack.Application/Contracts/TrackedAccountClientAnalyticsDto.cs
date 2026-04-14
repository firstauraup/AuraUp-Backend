namespace AuraUpBack.Application.Contracts;

public sealed record ClientAnalyticsSummaryDto(
    long CurrentFollowers,
    long LatestMonthFollowers,
    long PreviousMonthFollowers,
    int TotalReels,
    long TotalViews,
    long TotalLikes,
    long TotalComments,
    long TotalShares,
    long AverageViewsPerReel,
    decimal AverageEngagementRate);

public sealed record MonthlyFollowersPointDto(
    string MonthKey,
    DateTime MonthStartUtc,
    long FollowersCount,
    long GrowthFromPreviousMonth);

public sealed record MonthlyPerformancePointDto(
    string MonthKey,
    DateTime MonthStartUtc,
    int ReelCount,
    long Views,
    long Likes,
    long Comments,
    long Shares,
    long AverageViews,
    decimal AverageEngagementRate);

public sealed record TrackedAccountClientAnalyticsDto(
    Guid AccountId,
    string Handle,
    ClientAnalyticsSummaryDto Summary,
    IReadOnlyCollection<MonthlyFollowersPointDto> FollowersTimeline,
    IReadOnlyCollection<MonthlyPerformancePointDto> PerformanceTimeline);
