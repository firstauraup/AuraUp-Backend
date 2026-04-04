using AuraUpBack.Api.Realtime;
using AuraUpBack.Infrastructure.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace AuraUpBack.Api.Media;

internal sealed class ThumbnailCacheBackgroundService(
    IThumbnailCacheQueue thumbnailCacheQueue,
    IMediaAssetStorage mediaAssetStorage,
    IHubContext<AdminEventsHub> hubContext,
    ILogger<ThumbnailCacheBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var request = await thumbnailCacheQueue.DequeueAsync(stoppingToken);

            try
            {
                var cached = await mediaAssetStorage.WarmPostThumbnailAsync(request.AccountId, request.PostId, stoppingToken);
                if (cached)
                {
                    await hubContext.Clients.All.SendAsync(
                        "mediaReady",
                        new
                        {
                            accountId = request.AccountId,
                            postId = request.PostId,
                            mediaType = "thumbnail"
                        },
                        stoppingToken);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Thumbnail background generation failed for account {AccountId}, post {PostId}",
                    request.AccountId,
                    request.PostId);
            }
            finally
            {
                thumbnailCacheQueue.Complete(request);
            }
        }
    }
}
