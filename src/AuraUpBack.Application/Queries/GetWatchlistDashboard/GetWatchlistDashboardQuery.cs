using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Repositories;

namespace AuraUpBack.Application.Queries.GetWatchlistDashboard;

public sealed record GetWatchlistDashboardQuery(
    string Search = "",
    string SortBy = "bestMultiplier",
    long? MinViews = null,
    long? MinLikes = null,
    long? MinComments = null,
    long? MinShares = null) : Abstractions.IQuery<WatchlistDashboardDto>;

internal sealed class GetWatchlistDashboardQueryHandler(
    ITrackedAccountRepository trackedAccountRepository,
    IAlertSignalRepository alertSignalRepository)
    : Abstractions.IQueryHandler<GetWatchlistDashboardQuery, WatchlistDashboardDto>
{
    public async Task<WatchlistDashboardDto> HandleAsync(GetWatchlistDashboardQuery query, CancellationToken cancellationToken)
    {
        var accounts = await trackedAccountRepository.GetAllAsync(cancellationToken);
        var alerts = await alertSignalRepository.GetLatestAsync(20, cancellationToken);

        var accountItems = accounts
            .Select(account =>
            {
                var reels = account.GetReels();

                return new
                {
                    Account = account,
                    Item = new WatchlistAccountItemDto(
                    account.Id,
                    account.Handle,
                    account.DisplayName,
                    account.ProfileImageUrl,
                    account.MonitoringEnabled,
                    account.LastInspectedAtUtc,
                    reels.Count == 0 ? 0m : reels.Max(x => x.PerformanceMultiplier),
                    reels.Count == 0 ? 0 : reels.Max(x => x.Views),
                    reels.Count == 0 ? 0 : reels.Max(x => x.Likes),
                    reels.Count == 0 ? 0 : reels.Max(x => x.Comments),
                    reels.Count == 0 ? 0 : reels.Max(x => x.Shares),
                    reels.Count,
                    reels.Count(x => x.IsOutlier))
                };
            })
            .Where(x =>
                string.IsNullOrWhiteSpace(query.Search)
                || x.Account.Handle.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
                || x.Account.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            .Where(x => !query.MinViews.HasValue || x.Item.TopViews >= query.MinViews.Value)
            .Where(x => !query.MinLikes.HasValue || x.Item.TopLikes >= query.MinLikes.Value)
            .Where(x => !query.MinComments.HasValue || x.Item.TopComments >= query.MinComments.Value)
            .Where(x => !query.MinShares.HasValue || x.Item.TopShares >= query.MinShares.Value)
            .OrderByDescending(x => ResolveSortValue(x.Item, query.SortBy))
            .ThenByDescending(x => x.Item.LastInspectedAtUtc)
            .Select(x => x.Item)
            .ToList();

        var latestAlerts = alerts
            .Select(alert =>
            {
                var account = accounts.FirstOrDefault(item => item.Id == alert.AccountId);
                var matchingReel = account?.GetReels()
                    .FirstOrDefault(reel => string.Equals(reel.ExternalId, alert.ExternalPostId, StringComparison.OrdinalIgnoreCase));

                return new AlertSignalDto(
                    alert.Id,
                    alert.AccountId,
                    matchingReel?.Id,
                    account?.Handle ?? string.Empty,
                    alert.ExternalPostId,
                    alert.Title,
                    alert.Message,
                    alert.Severity,
                    alert.CreatedAtUtc);
            })
            .ToList();

        var topReels = accounts
            .SelectMany(account => account.GetReels()
                .Select(reel => new GlobalViralReelDto(
                    account.Id,
                    reel.Id,
                    account.Handle,
                    account.DisplayName,
                    reel.ExternalId,
                    reel.Caption,
                    reel.Url,
                    reel.ThumbnailUrl,
                    reel.Views,
                    reel.Likes,
                    reel.Comments,
                    reel.Shares,
                    reel.PerformanceMultiplier,
                    string.IsNullOrWhiteSpace(reel.Topic) ? "general" : reel.Topic,
                    string.IsNullOrWhiteSpace(reel.HookStyle) ? "Direct hook" : reel.HookStyle)))
            .OrderByDescending(x => x.Views)
            .ThenByDescending(x => x.Likes)
            .ThenByDescending(x => x.PerformanceMultiplier)
            .Take(12)
            .ToList();

        return new WatchlistDashboardDto(accountItems, latestAlerts, topReels);
    }

    private static decimal ResolveSortValue(WatchlistAccountItemDto item, string sortBy)
    {
        return sortBy.Trim().ToLowerInvariant() switch
        {
            "views" => item.TopViews,
            "likes" => item.TopLikes,
            "comments" => item.TopComments,
            "shares" => item.TopShares,
            "posts" => item.TotalPosts,
            "outliers" => item.OutlierPosts,
            _ => item.BestMultiplier
        };
    }
}
