using System.Net;
using System.Text.RegularExpressions;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace AuraUpBack.Infrastructure.Services;

public readonly record struct StoredImage(byte[] Bytes, string ContentType);

internal sealed class MinioMediaStorage(
    ITrackedAccountRepository trackedAccountRepository,
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    IOptions<MinioMediaOptions> options,
    ILogger<MinioMediaStorage> logger)
    : IMediaAssetStorage
{
    private readonly MinioMediaOptions _options = options.Value;
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private readonly IMinioClient _privateClient = CreateClient(options.Value.PrivateEndpoint, options.Value.RootUser, options.Value.RootPassword);
    private readonly IMinioClient _publicClient = CreateClient(options.Value.PublicEndpoint, options.Value.RootUser, options.Value.RootPassword);
    private volatile bool _bucketReady;

    public async Task WarmAccountMediaAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return;
        }

        var account = await trackedAccountRepository.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return;
        }

        var changed = await TryStoreAvatarAsync(account, cancellationToken);
        var uploadsChanged = 0;
        var reelPosts = account.Posts.Where(post => post.ShouldBeTreatedAsReel).ToList();

        await Parallel.ForEachAsync(
            reelPosts,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelUploads),
                CancellationToken = cancellationToken
            },
            async (post, token) =>
            {
                if (await TryStorePostThumbnailAsync(account, post, token))
                {
                    Interlocked.Exchange(ref uploadsChanged, 1);
                }
            });

        if (changed || uploadsChanged == 1)
        {
            await trackedAccountRepository.UpsertAsync(account, cancellationToken);
        }
    }

    public async Task<bool> WarmPostThumbnailAsync(Guid accountId, Guid postId, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return false;
        }

        var account = await trackedAccountRepository.GetByIdAsync(accountId, cancellationToken);
        var post = account?.Posts.FirstOrDefault(item => item.Id == postId);
        if (account is null || post is null)
        {
            return false;
        }

        var changed = await TryStorePostThumbnailAsync(account, post, cancellationToken);
        if (changed)
        {
            await trackedAccountRepository.UpsertAsync(account, cancellationToken);
        }

        return !string.IsNullOrWhiteSpace(post.ThumbnailObjectKey);
    }

    public async Task<string> GetSignedAvatarUrlAsync(Guid accountId, string sourceUrl, string objectKey, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return sourceUrl;
        }

        var effectiveObjectKey = objectKey;
        if (string.IsNullOrWhiteSpace(effectiveObjectKey))
        {
            effectiveObjectKey = await EnsureAvatarObjectKeyAsync(accountId, cancellationToken);
        }

        return await GetSignedUrlOrFallbackAsync(effectiveObjectKey, sourceUrl, cancellationToken);
    }

    public async Task<string> GetSignedPostThumbnailUrlAsync(
        Guid accountId,
        Guid postId,
        string sourceUrl,
        string objectKey,
        string reelUrl,
        CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return sourceUrl;
        }

        var effectiveObjectKey = objectKey;
        if (string.IsNullOrWhiteSpace(effectiveObjectKey))
        {
            effectiveObjectKey = await EnsurePostObjectKeyAsync(accountId, postId, cancellationToken);
        }

        return await GetSignedUrlOrFallbackAsync(effectiveObjectKey, string.IsNullOrWhiteSpace(sourceUrl) ? reelUrl : sourceUrl, cancellationToken);
    }

    private async Task<string> EnsureAvatarObjectKeyAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return string.Empty;
        }

        var changed = await TryStoreAvatarAsync(account, cancellationToken);
        if (changed)
        {
            await trackedAccountRepository.UpsertAsync(account, cancellationToken);
        }

        return account.ProfileImageObjectKey;
    }

    private async Task<string> EnsurePostObjectKeyAsync(Guid accountId, Guid postId, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(accountId, cancellationToken);
        var post = account?.Posts.FirstOrDefault(item => item.Id == postId);
        if (account is null || post is null)
        {
            return string.Empty;
        }

        var changed = await TryStorePostThumbnailAsync(account, post, cancellationToken);
        if (changed)
        {
            await trackedAccountRepository.UpsertAsync(account, cancellationToken);
        }

        return post.ThumbnailObjectKey;
    }

    private async Task<bool> TryStoreAvatarAsync(TrackedAccount account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.ProfileImageUrl))
        {
            return false;
        }

        var image = await TryDownloadImageAsync(account.ProfileImageUrl, cancellationToken);
        if (image is null)
        {
            return false;
        }

        await EnsureBucketExistsAsync(cancellationToken);
        var objectKey = BuildAvatarObjectKey(account.Id);
        await UploadAsync(objectKey, image.Value, cancellationToken);

        if (string.Equals(account.ProfileImageObjectKey, objectKey, StringComparison.Ordinal))
        {
            return false;
        }

        account.ProfileImageObjectKey = objectKey;
        return true;
    }

    private async Task<bool> TryStorePostThumbnailAsync(TrackedAccount account, TrackedPost post, CancellationToken cancellationToken)
    {
        var changed = false;
        var sourceUrl = post.ThumbnailUrl;

        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            sourceUrl = await TryResolveThumbnailFromReelAsync(post.Url, cancellationToken);
            if (!string.IsNullOrWhiteSpace(sourceUrl))
            {
                post.ThumbnailUrl = sourceUrl;
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return changed;
        }

        var image = await TryDownloadImageAsync(sourceUrl, cancellationToken);
        if (image is null)
        {
            return changed;
        }

        await EnsureBucketExistsAsync(cancellationToken);
        var objectKey = BuildThumbnailObjectKey(account.Id, post.Id);
        await UploadAsync(objectKey, image.Value, cancellationToken);

        if (!string.Equals(post.ThumbnailObjectKey, objectKey, StringComparison.Ordinal))
        {
            post.ThumbnailObjectKey = objectKey;
            changed = true;
        }

        return changed;
    }

    private async Task<string> GetSignedUrlOrFallbackAsync(string objectKey, string fallbackUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
        {
            return fallbackUrl;
        }

        var cacheKey = $"media:signed:{objectKey}";
        var signedUrl = await memoryCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, _options.SignedUrlMinutes - 1));
            return await _publicClient.PresignedGetObjectAsync(
                new PresignedGetObjectArgs()
                    .WithBucket(_options.BucketName)
                    .WithObject(objectKey)
                    .WithExpiry((int)TimeSpan.FromMinutes(_options.SignedUrlMinutes).TotalSeconds));
        });

        return string.IsNullOrWhiteSpace(signedUrl) ? fallbackUrl : signedUrl;
    }

    private async Task EnsureBucketExistsAsync(CancellationToken cancellationToken)
    {
        if (_bucketReady)
        {
            return;
        }

        await _bucketGate.WaitAsync(cancellationToken);

        try
        {
            if (_bucketReady)
            {
                return;
            }

            var exists = await _privateClient.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(_options.BucketName),
                cancellationToken);

            if (!exists)
            {
                await _privateClient.MakeBucketAsync(
                    new MakeBucketArgs().WithBucket(_options.BucketName),
                    cancellationToken);
            }

            _bucketReady = true;
        }
        finally
        {
            _bucketGate.Release();
        }
    }

    private async Task UploadAsync(string objectKey, StoredImage image, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(image.Bytes, writable: false);

        await _privateClient.PutObjectAsync(
            new PutObjectArgs()
                .WithBucket(_options.BucketName)
                .WithObject(objectKey)
                .WithStreamData(stream)
                .WithObjectSize(stream.Length)
                .WithContentType(image.ContentType),
            cancellationToken);
    }

    private async Task<StoredImage?> TryDownloadImageAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.instagram.com/");

            using var response = await client.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
            {
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return new StoredImage(bytes, contentType);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Unable to download image from {SourceUrl}", sourceUrl);
            return null;
        }
    }

    private async Task<string> TryResolveThumbnailFromReelAsync(string reelUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reelUrl))
        {
            return string.Empty;
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var response = await client.GetAsync(reelUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return ExtractMetaContent(html, "og:image");
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Unable to resolve thumbnail metadata for reel {ReelUrl}", reelUrl);
            return string.Empty;
        }
    }

    private static string ExtractMetaContent(string rawHtml, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            return string.Empty;
        }

        var propertyPattern = $@"<meta[^>]+property\s*=\s*[""']{Regex.Escape(propertyName)}[""'][^>]+content\s*=\s*[""'](?<value>.*?)[""']";
        var propertyMatch = Regex.Match(rawHtml, propertyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        if (propertyMatch.Success)
        {
            return WebUtility.HtmlDecode(propertyMatch.Groups["value"].Value);
        }

        var reversePattern = $@"<meta[^>]+content\s*=\s*[""'](?<value>.*?)[""'][^>]+property\s*=\s*[""']{Regex.Escape(propertyName)}[""']";
        var reverseMatch = Regex.Match(rawHtml, reversePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return reverseMatch.Success
            ? WebUtility.HtmlDecode(reverseMatch.Groups["value"].Value)
            : string.Empty;
    }

    private static string BuildAvatarObjectKey(Guid accountId) => $"accounts/{accountId:N}/profile/avatar";

    private static string BuildThumbnailObjectKey(Guid accountId, Guid postId) => $"accounts/{accountId:N}/posts/{postId:N}/thumbnail";

    private static IMinioClient CreateClient(string endpoint, string rootUser, string rootPassword)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new MinioClient()
                .WithEndpoint("localhost", 9000)
                .WithCredentials("minio", "minio123")
                .Build();
        }

        var uri = new Uri(endpoint);
        return new MinioClient()
            .WithEndpoint(uri.Host, uri.Port)
            .WithCredentials(rootUser, rootPassword)
            .WithSSL(uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            .Build();
    }
}
