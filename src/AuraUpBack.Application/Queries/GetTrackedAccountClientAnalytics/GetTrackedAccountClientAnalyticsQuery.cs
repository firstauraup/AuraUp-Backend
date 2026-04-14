using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Repositories;

namespace AuraUpBack.Application.Queries.GetTrackedAccountClientAnalytics;

public sealed record GetTrackedAccountClientAnalyticsQuery(Guid AccountId)
    : Abstractions.IQuery<TrackedAccountClientAnalyticsDto>;

internal sealed class GetTrackedAccountClientAnalyticsQueryHandler(ITrackedAccountRepository trackedAccountRepository)
    : Abstractions.IQueryHandler<GetTrackedAccountClientAnalyticsQuery, TrackedAccountClientAnalyticsDto>
{
    public async Task<TrackedAccountClientAnalyticsDto> HandleAsync(GetTrackedAccountClientAnalyticsQuery query, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(query.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked account was not found.");

        var reels = account.GetReels()
            .OrderBy(x => x.PublishedAtUtc)
            .ToList();

        var orderedSnapshots = account.MetricSnapshots
            .OrderBy(x => x.SnapshotMonthUtc)
            .ToList();

        var followerTimeline = new List<MonthlyFollowersPointDto>(orderedSnapshots.Count);
        for (var index = 0; index < orderedSnapshots.Count; index++)
        {
            var snapshot = orderedSnapshots[index];
            var previousSnapshotFollowers = index > 0 ? orderedSnapshots[index - 1].FollowersCount : snapshot.FollowersCount;
            followerTimeline.Add(new MonthlyFollowersPointDto(
                snapshot.SnapshotMonthUtc.ToString("yyyy-MM"),
                snapshot.SnapshotMonthUtc,
                snapshot.FollowersCount,
                snapshot.FollowersCount - previousSnapshotFollowers));
        }

        var performanceTimeline = reels
            .GroupBy(post => new DateTime(post.PublishedAtUtc.Year, post.PublishedAtUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var posts = group.ToList();
                var totalViews = posts.Sum(x => x.Views);
                var totalLikes = posts.Sum(x => x.Likes);
                var totalComments = posts.Sum(x => x.Comments);
                var totalShares = posts.Sum(x => x.Shares);
                var totalInteractions = totalLikes + totalComments + totalShares;
                var averageEngagementRate = totalViews <= 0
                    ? 0m
                    : Math.Round((decimal)totalInteractions / totalViews * 100m, 2, MidpointRounding.AwayFromZero);

                return new MonthlyPerformancePointDto(
                    group.Key.ToString("yyyy-MM"),
                    group.Key,
                    posts.Count,
                    totalViews,
                    totalLikes,
                    totalComments,
                    totalShares,
                    posts.Count == 0 ? 0 : (long)Math.Round(posts.Average(x => x.Views), MidpointRounding.AwayFromZero),
                    averageEngagementRate);
            })
            .ToList();

        var latestFollowers = followerTimeline.LastOrDefault()?.FollowersCount ?? account.FollowersCount;
        var previousFollowers = followerTimeline.Count > 1
            ? followerTimeline[^2].FollowersCount
            : latestFollowers;
        var totalViewsAll = reels.Sum(x => x.Views);
        var totalLikesAll = reels.Sum(x => x.Likes);
        var totalCommentsAll = reels.Sum(x => x.Comments);
        var totalSharesAll = reels.Sum(x => x.Shares);
        var totalInteractionsAll = totalLikesAll + totalCommentsAll + totalSharesAll;
        var averageEngagementRateAll = totalViewsAll <= 0
            ? 0m
            : Math.Round((decimal)totalInteractionsAll / totalViewsAll * 100m, 2, MidpointRounding.AwayFromZero);

        var summary = new ClientAnalyticsSummaryDto(
            account.FollowersCount,
            latestFollowers,
            previousFollowers,
            reels.Count,
            totalViewsAll,
            totalLikesAll,
            totalCommentsAll,
            totalSharesAll,
            reels.Count == 0 ? 0 : (long)Math.Round(reels.Average(x => x.Views), MidpointRounding.AwayFromZero),
            averageEngagementRateAll);

        return new TrackedAccountClientAnalyticsDto(
            account.Id,
            account.Handle,
            summary,
            followerTimeline,
            performanceTimeline);
    }
}
