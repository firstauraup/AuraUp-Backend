namespace AuraUpBack.Application.Contracts;

public sealed record TrackedAccountOverviewDto(
    Guid Id,
    string Handle,
    string DisplayName,
    string ProfileImageUrl,
    string ProfileImageObjectKey,
    string Bio,
    long FollowersCount,
    bool MonitoringEnabled,
    string MonitoringPrompt,
    int CheckEveryMinutes,
    int OutlierNotificationMultiplier,
    string LastResearchSummary,
    DateTime? LastInspectedAtUtc,
    IReadOnlyCollection<PostSummaryDto> Posts);
