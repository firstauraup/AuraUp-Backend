namespace AuraUpBack.Application.Contracts;

public sealed record ExplorerAccountPreviewDto(
    string Handle,
    string DisplayName,
    string ProfileImageUrl,
    string Bio,
    long FollowersCount);

public sealed record ExplorerReelDto(
    string ExternalId,
    string Caption,
    string Url,
    string ThumbnailUrl,
    DateTime? PublishedAtUtc,
    long Views,
    long Likes,
    long Comments,
    long Shares,
    ExplorerAccountPreviewDto Account);

public sealed record ExplorerSearchResultDto(
    string Query,
    int Page,
    int PageSize,
    int ReturnedCount,
    bool HasMore,
    IReadOnlyCollection<ExplorerReelDto> Reels);
