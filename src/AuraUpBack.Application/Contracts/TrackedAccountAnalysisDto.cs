namespace AuraUpBack.Application.Contracts;

public sealed record ViralPostDto(
    Guid Id,
    string ExternalId,
    string Caption,
    string Url,
    DateTime PublishedAtUtc,
    long Views,
    decimal PerformanceMultiplier,
    string Topic,
    string HookStyle,
    string ThemeSummary);

public sealed record TopicPerformanceDto(
    string Topic,
    int TotalPosts,
    int ViralPosts,
    long AverageViews,
    decimal AverageMultiplier,
    string BestHookStyle,
    string ThemeSummary);

public sealed record TrackedAccountAnalysisDto(
    Guid AccountId,
    string Handle,
    int TotalPosts,
    long AverageViews,
    long MedianViews,
    decimal BaselineMultiplier,
    int ViralPosts,
    IReadOnlyCollection<ViralPostDto> TopVirals,
    IReadOnlyCollection<TopicPerformanceDto> TopTopics);

public sealed record BackfillTrackedAccountHistoryDto(
    Guid AccountId,
    string Handle,
    int ExecutedBatches,
    int TotalNewPosts,
    bool ReachedEndOfHistory,
    DateTime CompletedAtUtc);
