using System.Collections.Generic;

namespace AuraUpBack.Api.Media;

internal sealed class InMemoryThumbnailCacheQueue : IThumbnailCacheQueue
{
    private readonly Lock _sync = new();
    private readonly Queue<ThumbnailCacheRequest> _queue = new();
    private readonly HashSet<ThumbnailCacheRequest> _pending = [];
    private readonly SemaphoreSlim _signal = new(0);

    public bool Enqueue(Guid accountId, Guid postId)
    {
        var request = new ThumbnailCacheRequest(accountId, postId);
        var queued = false;

        lock (_sync)
        {
            if (_pending.Add(request))
            {
                _queue.Enqueue(request);
                queued = true;
            }
        }

        if (queued)
        {
            _signal.Release();
        }

        return queued;
    }

    public async ValueTask<ThumbnailCacheRequest> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken);

            lock (_sync)
            {
                if (_queue.TryDequeue(out var request))
                {
                    return request;
                }
            }
        }
    }

    public void Complete(ThumbnailCacheRequest request)
    {
        lock (_sync)
        {
            _pending.Remove(request);
        }
    }
}
