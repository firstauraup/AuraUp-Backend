using AuraUpBack.Application.Contracts;
using AuraUpBack.Application.Mappers;
using AuraUpBack.Domain.Repositories;

namespace AuraUpBack.Application.Queries.GetTrackedAccountOverview;

public sealed record GetTrackedAccountOverviewQuery(
    Guid AccountId,
    DateTime? FromUtc,
    DateTime? ToUtc,
    string Search = "",
    string SortBy = "performance",
    long? MinViews = null,
    long? MinLikes = null,
    long? MinComments = null,
    long? MinShares = null)
    : Abstractions.IQuery<TrackedAccountOverviewDto>;

internal sealed class GetTrackedAccountOverviewQueryHandler(ITrackedAccountRepository trackedAccountRepository)
    : Abstractions.IQueryHandler<GetTrackedAccountOverviewQuery, TrackedAccountOverviewDto>
{
    public async Task<TrackedAccountOverviewDto> HandleAsync(GetTrackedAccountOverviewQuery query, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(query.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked account was not found.");

        var overview = account.ToOverviewDto(query.FromUtc, query.ToUtc);
        var filteredPosts = overview.Posts
            .Where(post =>
                string.IsNullOrWhiteSpace(query.Search)
                || post.Caption.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
                || post.ExternalId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
                || post.Topic.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
                || post.HookStyle.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            .Where(post => !query.MinViews.HasValue || post.Views >= query.MinViews.Value)
            .Where(post => !query.MinLikes.HasValue || post.Likes >= query.MinLikes.Value)
            .Where(post => !query.MinComments.HasValue || post.Comments >= query.MinComments.Value)
            .Where(post => !query.MinShares.HasValue || post.Shares >= query.MinShares.Value)
            .OrderByDescending(post => ResolveSortValue(post, query.SortBy))
            .ThenByDescending(post => post.PublishedAtUtc)
            .ToList();

        return overview with { Posts = filteredPosts };
    }

    private static decimal ResolveSortValue(PostSummaryDto post, string sortBy)
    {
        return sortBy.Trim().ToLowerInvariant() switch
        {
            "views" => post.Views,
            "likes" => post.Likes,
            "comments" => post.Comments,
            "shares" => post.Shares,
            "published" => post.PublishedAtUtc.Ticks,
            _ => post.PerformanceMultiplier
        };
    }
}
