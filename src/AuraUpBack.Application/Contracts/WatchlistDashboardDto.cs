namespace AuraUpBack.Application.Contracts;

public sealed record WatchlistAccountItemDto(
    Guid AccountId,
    string Handle,
    string DisplayName,
    string ProfileImageUrl,
    string ProfileImageObjectKey,
    bool MonitoringEnabled,
    int CheckEveryMinutes,
    int OutlierNotificationMultiplier,
    DateTime? LastInspectedAtUtc,
    decimal BestMultiplier,
    long TopViews,
    long TopLikes,
    long TopComments,
    long TopShares,
    int TotalPosts,
    int OutlierPosts);

public sealed record GlobalViralReelDto(
    Guid AccountId,
    Guid PostId,
    string AccountHandle,
    string AccountDisplayName,
    string ExternalId,
    string Caption,
    string Url,
    string ThumbnailUrl,
    string ThumbnailObjectKey,
    long Views,
    long Likes,
    long Comments,
    long Shares,
    decimal PerformanceMultiplier,
    string Topic,
    string HookStyle);

public sealed record AlertSignalDto(
    Guid Id,
    Guid AccountId,
    Guid? PostId,
    string AccountHandle,
    string ExternalPostId,
    string Title,
    string Message,
    string Severity,
    int NotificationMultiplier,
    DateTime CreatedAtUtc);

public sealed record WatchlistDashboardDto(
    IReadOnlyCollection<WatchlistAccountItemDto> Accounts,
    IReadOnlyCollection<AlertSignalDto> LatestAlerts,
    IReadOnlyCollection<GlobalViralReelDto> TopReels);
