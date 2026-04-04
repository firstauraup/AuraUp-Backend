using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Repositories;

namespace AuraUpBack.Application.Queries.GetTrackedAccountAnalysis;

public sealed record GetTrackedAccountAnalysisQuery(
    Guid AccountId,
    string SortBy = "performance",
    long? MinViews = null,
    long? MinLikes = null,
    long? MinComments = null,
    long? MinShares = null) : Abstractions.IQuery<TrackedAccountAnalysisDto>;

internal sealed class GetTrackedAccountAnalysisQueryHandler(ITrackedAccountRepository trackedAccountRepository)
    : Abstractions.IQueryHandler<GetTrackedAccountAnalysisQuery, TrackedAccountAnalysisDto>
{
    public async Task<TrackedAccountAnalysisDto> HandleAsync(GetTrackedAccountAnalysisQuery query, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(query.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked account was not found.");

        var orderedPosts = account.GetReels()
            .Where(post => !query.MinViews.HasValue || post.Views >= query.MinViews.Value)
            .Where(post => !query.MinLikes.HasValue || post.Likes >= query.MinLikes.Value)
            .Where(post => !query.MinComments.HasValue || post.Comments >= query.MinComments.Value)
            .Where(post => !query.MinShares.HasValue || post.Shares >= query.MinShares.Value)
            .OrderByDescending(post => ResolveSortValue(post, query.SortBy))
            .ThenByDescending(post => post.PublishedAtUtc)
            .ToList();

        var totalPosts = orderedPosts.Count;
        var views = orderedPosts.Select(x => x.Views).OrderBy(x => x).ToArray();
        var averageViews = totalPosts == 0 ? 0 : (long)Math.Round(orderedPosts.Average(x => x.Views), MidpointRounding.AwayFromZero);
        var medianViews = totalPosts == 0
            ? 0
            : views.Length % 2 == 1
                ? views[views.Length / 2]
                : (views[(views.Length / 2) - 1] + views[views.Length / 2]) / 2;

        var topVirals = orderedPosts
            .Where(x => x.IsOutlier)
            .Take(10)
            .Select(x => new ViralPostDto(
                x.Id,
                x.ExternalId,
                x.Caption,
                x.Url,
                x.PublishedAtUtc,
                x.Views,
                x.PerformanceMultiplier,
                string.IsNullOrWhiteSpace(x.Topic) ? "general" : x.Topic,
                x.HookStyle,
                x.ThemeSummary))
            .ToList();

        var topTopics = orderedPosts
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Topic) ? "general" : x.Topic, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var posts = group.ToList();
                var bestHookStyle = posts
                    .GroupBy(x => string.IsNullOrWhiteSpace(x.HookStyle) ? "Direct hook" : x.HookStyle)
                    .OrderByDescending(x => x.Count())
                    .ThenByDescending(x => x.Max(post => post.PerformanceMultiplier))
                    .Select(x => x.Key)
                    .FirstOrDefault() ?? "Direct hook";

                var themeSummary = posts
                    .Select(x => x.ThemeSummary)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                    ?? "General creator content.";

                return new TopicPerformanceDto(
                    group.Key,
                    posts.Count,
                    posts.Count(x => x.IsOutlier),
                    (long)Math.Round(posts.Average(x => x.Views), MidpointRounding.AwayFromZero),
                    Math.Round(posts.Average(x => x.PerformanceMultiplier), 2, MidpointRounding.AwayFromZero),
                    bestHookStyle,
                    themeSummary);
            })
            .OrderByDescending(x => x.ViralPosts)
            .ThenByDescending(x => x.AverageMultiplier)
            .ThenByDescending(x => x.TotalPosts)
            .Take(10)
            .ToList();

        return new TrackedAccountAnalysisDto(
            account.Id,
            account.Handle,
            totalPosts,
            averageViews,
            medianViews,
            1m,
            orderedPosts.Count(x => x.IsOutlier),
            topVirals,
            topTopics);
    }

    private static decimal ResolveSortValue(Domain.Entities.TrackedPost post, string sortBy)
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
