using AuraUpBack.Domain.Repositories;
using Microsoft.Playwright;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuraUpBack.Api.Media;

public readonly record struct DownloadedImage(byte[] Bytes, string ContentType);

public sealed record CachedImageMetadata(string SourceUrl, string ContentType);

internal sealed class ThumbnailProxyService(
    ITrackedAccountRepository trackedAccountRepository,
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment environment)
{
    private readonly string _mediaCacheRoot = Path.Combine(environment.ContentRootPath, "App_Data", "MediaCache");
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    public string GetThumbnailCacheDirectory(Guid accountId, Guid postId)
    {
        return Path.Combine(_mediaCacheRoot, "accounts", accountId.ToString("N"), "posts", postId.ToString("N"));
    }

    public async Task<DownloadedImage?> TryReadCachedImageAsync(
        string cacheDirectory,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(cacheDirectory, "meta.json");
        var imagePath = Path.Combine(cacheDirectory, "image.bin");

        if (!File.Exists(metaPath) || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            var metaJson = await File.ReadAllTextAsync(metaPath, cancellationToken);
            var metadata = JsonSerializer.Deserialize<CachedImageMetadata>(metaJson);
            if (metadata is null ||
                !string.Equals(metadata.SourceUrl, sourceUrl, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(metadata.ContentType))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
            return bytes.Length == 0 ? null : new DownloadedImage(bytes, metadata.ContentType);
        }
        catch
        {
            return null;
        }
    }

    public async Task<DownloadedImage?> TryReadAnyCachedImageAsync(
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(cacheDirectory, "meta.json");
        var imagePath = Path.Combine(cacheDirectory, "image.bin");

        if (!File.Exists(metaPath) || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            var metaJson = await File.ReadAllTextAsync(metaPath, cancellationToken);
            var metadata = JsonSerializer.Deserialize<CachedImageMetadata>(metaJson);
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.ContentType))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
            return bytes.Length == 0 ? null : new DownloadedImage(bytes, metadata.ContentType);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> EnsureThumbnailCachedAsync(Guid accountId, Guid postId, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(accountId, cancellationToken);
        var post = account?.Posts.FirstOrDefault(x => x.Id == postId);
        if (post is null)
        {
            return false;
        }

        var cacheDirectory = GetThumbnailCacheDirectory(accountId, postId);
        if (await TryReadAnyCachedImageAsync(cacheDirectory, cancellationToken) is not null)
        {
            return true;
        }

        var sourceUrl = post.ThumbnailUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            sourceUrl = await TryResolveThumbnailFromReelAsync(post.Url, cancellationToken);
            if (!string.IsNullOrWhiteSpace(sourceUrl) && account is not null)
            {
                post.ThumbnailUrl = sourceUrl;
                await trackedAccountRepository.UpsertAsync(account, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            var downloadedImage = await TryDownloadImageAsync(sourceUrl, cancellationToken);
            if (downloadedImage is not null)
            {
                await SaveCachedImageAsync(cacheDirectory, sourceUrl, downloadedImage.Value, cancellationToken);
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(post.Url))
        {
            return false;
        }

        var screenshotBytes = await TryCaptureThumbnailFromReelAsync(post.Url, cancellationToken);
        if (screenshotBytes.Length == 0)
        {
            return false;
        }

        await SaveCachedImageAsync(
            cacheDirectory,
            string.IsNullOrWhiteSpace(sourceUrl) ? post.Url : sourceUrl,
            new DownloadedImage(screenshotBytes, "image/jpeg"),
            cancellationToken);

        return true;
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
        catch
        {
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

    public async Task<DownloadedImage?> TryDownloadImageAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.instagram.com/");

            using var response = await client.GetAsync(sourceUrl, cancellationToken);
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
            return new DownloadedImage(bytes, contentType);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveCachedImageAsync(
        string cacheDirectory,
        string sourceUrl,
        DownloadedImage image,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);

        var metaPath = Path.Combine(cacheDirectory, "meta.json");
        var imagePath = Path.Combine(cacheDirectory, "image.bin");
        var metadata = new CachedImageMetadata(sourceUrl, image.ContentType);

        await File.WriteAllBytesAsync(imagePath, image.Bytes, cancellationToken);
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(metadata), cancellationToken);
    }

    private async Task<byte[]> TryCaptureThumbnailFromReelAsync(string reelUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reelUrl))
        {
            return [];
        }

        var acquired = false;

        try
        {
            acquired = await _captureGate.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            if (!acquired)
            {
                return [];
            }

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                ChromiumSandbox = false,
                Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage", "--disable-gpu"]
            });

            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 720, Height = 1280 }
            });

            var page = await context.NewPageAsync();
            await page.GotoAsync(reelUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 8000
            });

            await page.WaitForTimeoutAsync(1000);

            var media = page.Locator("article img, article video, main img, main video").First;
            if (await media.CountAsync() > 0)
            {
                return await media.ScreenshotAsync(new LocatorScreenshotOptions
                {
                    Type = ScreenshotType.Jpeg,
                    Quality = 85
                });
            }

            return await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Jpeg,
                Quality = 80,
                FullPage = false
            });
        }
        catch
        {
            return [];
        }
        finally
        {
            if (acquired)
            {
                _captureGate.Release();
            }
        }
    }
}
