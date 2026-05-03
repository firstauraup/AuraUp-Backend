using AuraUpBack.Domain.Models;

namespace AuraUpBack.Domain.Services;

public interface IInstagramExplorerService
{
    Task<ExplorerSearchResult> SearchReelsAsync(
        string query,
        int page,
        int pageSize,
        string sortBy,
        long? minViews,
        long? minLikes,
        long? minComments,
        long? minShares,
        CancellationToken cancellationToken);

    Task<ExplorerAccountPreview> GetAccountPreviewAsync(string handle, CancellationToken cancellationToken);

    Task<ExplorerReel?> GetReelAsync(string? reelUrl, CancellationToken cancellationToken);

    Task<ExplorerAccountSnapshot> GetAccountSnapshotAsync(
        string handle,
        int reelCount,
        CancellationToken cancellationToken);
}
