using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Queries.SearchInstagramExplorer;

public sealed record SearchInstagramExplorerQuery(
    string Query,
    int Page,
    int PageSize,
    string SortBy,
    long? MinViews,
    long? MinLikes,
    long? MinComments,
    long? MinShares) : Abstractions.IQuery<ExplorerSearchResultDto>;

internal sealed class SearchInstagramExplorerQueryHandler(
    IInstagramExplorerService instagramExplorerService)
    : Abstractions.IQueryHandler<SearchInstagramExplorerQuery, ExplorerSearchResultDto>
{
    public async Task<ExplorerSearchResultDto> HandleAsync(SearchInstagramExplorerQuery query, CancellationToken cancellationToken)
    {
        var result = await instagramExplorerService.SearchReelsAsync(
            query.Query,
            query.Page,
            query.PageSize,
            query.SortBy,
            query.MinViews,
            query.MinLikes,
            query.MinComments,
            query.MinShares,
            cancellationToken);

        return new ExplorerSearchResultDto(
            result.Query,
            result.Page,
            result.PageSize,
            result.ReturnedCount,
            result.HasMore,
            result.Reels.Select(reel => new ExplorerReelDto(
                reel.ExternalId,
                reel.Caption,
                reel.Url,
                reel.ThumbnailUrl,
                reel.PublishedAtUtc,
                reel.Views,
                reel.Likes,
                reel.Comments,
                reel.Shares,
                new ExplorerAccountPreviewDto(
                    reel.Account.Handle,
                    reel.Account.DisplayName,
                    reel.Account.ProfileImageUrl,
                    reel.Account.Bio,
                    reel.Account.FollowersCount)))
                .ToList());
    }
}
