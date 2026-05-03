namespace AuraUpBack.Domain.Models;

public sealed record ExplorerAccountPreview(
    string Handle,
    string DisplayName,
    string ProfileImageUrl,
    string Bio,
    long FollowersCount);

public sealed record ExplorerReel(
    string ExternalId,
    string Caption,
    string Url,
    string ThumbnailUrl,
    DateTime? PublishedAtUtc,
    long Views,
    long Likes,
    long Comments,
    long Shares,
    ExplorerAccountPreview Account);

public sealed record ExplorerSearchResult(
    string Query,
    int Page,
    int PageSize,
    int ReturnedCount,
    bool HasMore,
    IReadOnlyCollection<ExplorerReel> Reels);

public sealed record ExplorerAccountSnapshot(
    ExplorerAccountPreview Account,
    IReadOnlyCollection<ExplorerReel> Reels);
