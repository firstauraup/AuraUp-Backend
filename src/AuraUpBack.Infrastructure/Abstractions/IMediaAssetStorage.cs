namespace AuraUpBack.Infrastructure.Abstractions;

public interface IMediaAssetStorage
{
    Task WarmAccountMediaAsync(Guid accountId, CancellationToken cancellationToken);
    Task<bool> WarmPostThumbnailAsync(Guid accountId, Guid postId, CancellationToken cancellationToken);
    Task<string> GetSignedAvatarUrlAsync(Guid accountId, string sourceUrl, string objectKey, CancellationToken cancellationToken);
    Task<string> GetSignedPostThumbnailUrlAsync(Guid accountId, Guid postId, string sourceUrl, string objectKey, string reelUrl, CancellationToken cancellationToken);
}
