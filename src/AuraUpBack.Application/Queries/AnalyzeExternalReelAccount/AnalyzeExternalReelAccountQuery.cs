using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Exceptions;
using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Queries.AnalyzeExternalReelAccount;

public sealed record AnalyzeExternalReelAccountQuery(string? ReelUrl)
    : Abstractions.IQuery<ExplorerAccountSnapshotDto>;

internal sealed class AnalyzeExternalReelAccountQueryHandler(
    IInstagramExplorerService instagramExplorerService)
    : Abstractions.IQueryHandler<AnalyzeExternalReelAccountQuery, ExplorerAccountSnapshotDto>
{
    public async Task<ExplorerAccountSnapshotDto> HandleAsync(AnalyzeExternalReelAccountQuery query, CancellationToken cancellationToken)
    {
        var reel = await instagramExplorerService.GetReelAsync(query.ReelUrl, cancellationToken)
            ?? throw new InvalidOperationException("The reel account could not be detected.");

        if (string.IsNullOrWhiteSpace(reel.Account.Handle) ||
            string.Equals(reel.Account.Handle, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The reel account could not be detected.");
        }

        try
        {
            var snapshot = await instagramExplorerService.GetAccountSnapshotAsync(reel.Account.Handle, 5, cancellationToken);
            var reels = snapshot.Reels.Select(ToDto).ToList();
            if (!reels.Any(x => string.Equals(x.ExternalId, reel.ExternalId, StringComparison.OrdinalIgnoreCase)))
            {
                reels.Insert(0, ToDto(reel));
            }

            return new ExplorerAccountSnapshotDto(
                ToDto(snapshot.Account),
                reels);
        }
        catch (InstagramRateLimitException)
        {
            return new ExplorerAccountSnapshotDto(
                ToDto(reel.Account),
                new[] { ToDto(reel) });
        }
    }

    private static ExplorerAccountPreviewDto ToDto(ExplorerAccountPreview account)
    {
        return new ExplorerAccountPreviewDto(
            account.Handle,
            account.DisplayName,
            account.ProfileImageUrl,
            account.Bio,
            account.FollowersCount);
    }

    private static ExplorerReelDto ToDto(ExplorerReel reel)
    {
        return new ExplorerReelDto(
            reel.ExternalId,
            reel.Caption,
            reel.Url,
            reel.ThumbnailUrl,
            reel.PublishedAtUtc,
            reel.Views,
            reel.Likes,
            reel.Comments,
            reel.Shares,
            ToDto(reel.Account));
    }
}
