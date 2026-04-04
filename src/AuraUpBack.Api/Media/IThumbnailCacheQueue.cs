namespace AuraUpBack.Api.Media;

public interface IThumbnailCacheQueue
{
    bool Enqueue(Guid accountId, Guid postId);
    ValueTask<ThumbnailCacheRequest> DequeueAsync(CancellationToken cancellationToken);
    void Complete(ThumbnailCacheRequest request);
}

public readonly record struct ThumbnailCacheRequest(Guid AccountId, Guid PostId);
