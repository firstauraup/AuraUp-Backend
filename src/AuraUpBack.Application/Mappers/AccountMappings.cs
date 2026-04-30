using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Entities;

namespace AuraUpBack.Application.Mappers;

internal static class AccountMappings
{
    public static TrackedAccountOverviewDto ToOverviewDto(this TrackedAccount account, DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        var posts = account
            .GetPostsWithin(fromUtc, toUtc)
            .Select(x => new PostSummaryDto(
                x.Id,
                x.ExternalId,
                x.Caption,
                x.Url,
                x.ThumbnailUrl,
                x.ThumbnailObjectKey,
                x.PublishedAtUtc,
                x.Views,
                x.Likes,
                x.Comments,
                x.Shares,
                x.IgPlayCount,
                x.FbPlayCount,
                x.FbLikes,
                x.FbComments,
                x.PerformanceMultiplier,
                x.IsOutlier,
                x.PerformanceLabel,
                x.Transcript,
                x.Topic,
                x.TopicConfidence,
                x.ContentAngle,
                x.HookStyle,
                x.ThemeSummary))
            .ToList();

        return new TrackedAccountOverviewDto(
            account.Id,
            account.Handle,
            account.DisplayName,
            account.ProfileImageUrl,
            account.ProfileImageObjectKey,
            account.Bio,
            account.FollowersCount,
            account.MonitoringEnabled,
            account.MonitoringPrompt,
            account.CheckEveryMinutes,
            account.LastResearchSummary,
            account.LastInspectedAtUtc,
            posts);
    }

    public static ExplorationRequestDto ToDto(this ExplorationRequest request)
    {
        return new ExplorationRequestDto(
            request.Id,
            request.AccountHandle,
            request.ResearchPrompt,
            request.Status,
            request.Summary,
            request.CreatedAtUtc,
            request.LastRunAtUtc,
            request.SelectedPostExternalIds);
    }
}
