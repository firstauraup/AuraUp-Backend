using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using AuraUpBack.Domain.Enums;
using AuraUpBack.Domain.Exceptions;
using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Services;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace AuraUpBack.Infrastructure.Services;

internal sealed partial class InstagramExplorerService(
    IInstagramConnectionAutomation instagramConnectionAutomation,
    InstagramBrowserProfileService browserProfileService,
    IMemoryCache memoryCache,
    IOptions<InstagramIntegrationOptions> options,
    ILogger<InstagramExplorerService> logger)
    : IInstagramExplorerService
{
    private static readonly HttpClient SnapshotHttpClient = CreateSnapshotHttpClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SearchLocks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PreviewLocks = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ReservedInstagramPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts",
        "direct",
        "explore",
        "p",
        "reel",
        "reels",
        "stories",
        "tv"
    };
    private readonly InstagramIntegrationOptions _options = options.Value;
    private readonly SemaphoreSlim _searchSlots = new(Math.Max(1, options.Value.ExplorerMaxConcurrentSearches));

    public async Task<ExplorerSearchResult> SearchReelsAsync(
        string query,
        int page,
        int pageSize,
        string sortBy,
        long? minViews,
        long? minLikes,
        long? minComments,
        long? minShares,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            throw new InvalidOperationException("The explorer query is required.");
        }

        var normalizedPage = Math.Max(1, page);
        var normalizedPageSize = Math.Clamp(pageSize, 1, 50);
        var requiredResults = normalizedPage * normalizedPageSize;
        var cacheKey = BuildCacheKey(normalizedQuery, sortBy, minViews, minLikes, minComments, minShares);
        var cached = await GetOrCreateCachedSearchAsync(
            cacheKey,
            normalizedQuery,
            sortBy,
            minViews,
            minLikes,
            minComments,
            minShares,
            Math.Max(requiredResults, normalizedPageSize * 4),
            requiredResults,
            cancellationToken);

        var filtered = cached.Reels;

        var skip = (normalizedPage - 1) * normalizedPageSize;
        var pageItems = filtered.Skip(skip).Take(normalizedPageSize).ToList();
        var hasMore = filtered.Count > skip + pageItems.Count || (cached.MayHaveMore && cached.CoveredResults > skip + pageItems.Count);

        return new ExplorerSearchResult(
            normalizedQuery,
            normalizedPage,
            normalizedPageSize,
            pageItems.Count,
            hasMore,
            pageItems);
    }

    public async Task<ExplorerAccountPreview> GetAccountPreviewAsync(string handle, CancellationToken cancellationToken)
    {
        var normalizedHandle = handle.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedHandle))
        {
            throw new InvalidOperationException("The account handle is required.");
        }

        var cacheKey = $"explorer-preview:{normalizedHandle}";
        if (memoryCache.TryGetValue<ExplorerAccountPreview>(cacheKey, out var cachedPreview) &&
            cachedPreview is not null)
        {
            return cachedPreview;
        }

        var previewLock = PreviewLocks.GetOrAdd(normalizedHandle, static _ => new SemaphoreSlim(1, 1));
        await previewLock.WaitAsync(cancellationToken);

        try
        {
            if (memoryCache.TryGetValue<ExplorerAccountPreview>(cacheKey, out cachedPreview) &&
                cachedPreview is not null)
            {
                return cachedPreview;
            }

            var rawHtml = await ReadPreviewHtmlAsync(normalizedHandle, cancellationToken);
            var description = ExtractMetaContent(rawHtml, "og:description");
            var title = ExtractMetaContent(rawHtml, "og:title", ExtractHtmlTitle(rawHtml));

            var preview = new ExplorerAccountPreview(
                normalizedHandle,
                ExtractDisplayName(title, normalizedHandle),
                ExtractMetaContent(rawHtml, "og:image"),
                ExtractBio(description, normalizedHandle),
                TryParseFollowerCount(description));

            memoryCache.Set(
                cacheKey,
                preview,
                TimeSpan.FromMinutes(Math.Max(1, _options.ExplorerPreviewCacheMinutes)));

            return preview;
        }
        finally
        {
            previewLock.Release();
        }
    }

    public async Task<ExplorerReel?> GetReelAsync(string? reelUrl, CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeInstagramReelUrl(reelUrl);
        var cacheKey = $"explorer-reel:{normalizedUrl}";
        if (memoryCache.TryGetValue<ExplorerReel>(cacheKey, out var cachedReel) &&
            cachedReel is not null)
        {
            return cachedReel;
        }

        var reel = await TryReadReelFromSnapshotHtmlAsync(normalizedUrl, cancellationToken);
        reel ??= await WithExplorerContextAsync(
            async context => await ReadReelAsync(context, normalizedUrl, cancellationToken),
            cancellationToken);

        if (reel is not null)
        {
            memoryCache.Set(
                cacheKey,
                reel,
                TimeSpan.FromMinutes(Math.Max(1, _options.ExplorerPreviewCacheMinutes)));
        }

        return reel;
    }

    public async Task<ExplorerAccountSnapshot> GetAccountSnapshotAsync(
        string handle,
        int reelCount,
        CancellationToken cancellationToken)
    {
        var normalizedHandle = handle.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedHandle))
        {
            throw new InvalidOperationException("The account handle is required.");
        }

        var normalizedReelCount = Math.Clamp(reelCount, 1, 12);
        var cacheKey = $"explorer-account-snapshot:{normalizedHandle}:{normalizedReelCount}";
        if (memoryCache.TryGetValue<ExplorerAccountSnapshot>(cacheKey, out var cachedSnapshot) &&
            cachedSnapshot is not null)
        {
            return cachedSnapshot;
        }

        var preview = await GetAccountPreviewAsync(normalizedHandle, cancellationToken);
        var reels = await WithExplorerContextAsync(
            async context => await ReadRecentAccountReelsAsync(
                context,
                normalizedHandle,
                preview,
                normalizedReelCount,
                cancellationToken),
            cancellationToken);

        var snapshot = new ExplorerAccountSnapshot(preview, reels);
        memoryCache.Set(
            cacheKey,
            snapshot,
            TimeSpan.FromMinutes(Math.Max(1, _options.ExplorerPreviewCacheMinutes)));
        return snapshot;
    }

    private async Task<ExplorerSessionContext> ResolveExplorerSessionAsync(CancellationToken cancellationToken)
    {
        var state = await instagramConnectionAutomation.GetStatusAsync(cancellationToken);
        var hasSession = state.Status == InstagramConnectionStatus.Connected && state.SessionStateExists;

        if (!hasSession && !state.AllowPublicProfileReadWithoutSession)
        {
            throw new InvalidOperationException("Explorer requires a valid Instagram session or public profile mode enabled.");
        }

        return new ExplorerSessionContext(hasSession, state.SessionStatePath);
    }

    private static async Task<IBrowserContext> CreateContextAsync(IBrowser browser, ExplorerSessionContext session)
    {
        return await browser.NewContextAsync(new BrowserNewContextOptions
        {
            StorageStatePath = session.HasSession ? session.SessionStatePath : null,
            ViewportSize = new ViewportSize { Width = 1440, Height = 980 }
        });
    }

    private async Task<T> WithExplorerContextAsync<T>(
        Func<IBrowserContext, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _searchSlots.WaitAsync(cancellationToken);

        try
        {
            var session = await ResolveExplorerSessionAsync(cancellationToken);
            var usePersistentProfile = session.HasSession;
            InstagramBrowserProfileService.PersistentBrowserLease? persistentLease = null;
            IPlaywright? playwright = null;
            IBrowser? browser = null;
            IBrowserContext? context = null;

            try
            {
                if (usePersistentProfile)
                {
                    persistentLease = await browserProfileService.AcquireAsync(_options.RpaHeadless, cancellationToken);
                    context = persistentLease.Context;
                    await InstagramBrowserProfileService.ExportSessionStateAsync(context, session.SessionStatePath);
                }
                else
                {
                    playwright = await Playwright.CreateAsync();
                    browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = _options.RpaHeadless,
                        ChromiumSandbox = false,
                        Args =
                        [
                            "--no-sandbox",
                            "--disable-setuid-sandbox",
                            "--disable-gpu",
                            "--disable-dev-shm-usage"
                        ]
                    });

                    context = await CreateContextAsync(browser, session);
                }

                return await action(context);
            }
            finally
            {
                if (persistentLease is not null)
                {
                    await persistentLease.DisposeAsync();
                }

                if (context is not null && !usePersistentProfile)
                {
                    await context.CloseAsync();
                }

                if (browser is not null)
                {
                    await browser.CloseAsync();
                }

                playwright?.Dispose();
            }
        }
        finally
        {
            _searchSlots.Release();
        }
    }

    private static async Task<IReadOnlyCollection<string>> ReadVisibleReelLinksAsync(IPage page)
    {
        var urls = await page.EvaluateAsync<string[]>(
            """
            () => Array.from(document.querySelectorAll('a[href]'))
              .map((anchor) => anchor.href || '')
              .filter((href) => /\/(reel|tv|p)\//i.test(href))
            """);

        return urls
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<ExplorerReel?> TryReadReelFromSnapshotHtmlAsync(string normalizedUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SnapshotHttpClient.GetAsync(
                normalizedUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var rawHtml = await ReadHtmlPrefixAsync(response.Content, cancellationToken);
            var title = ExtractMetaContent(rawHtml, "og:title", ExtractHtmlTitle(rawHtml));
            var description = ExtractMetaContent(rawHtml, "og:description");
            var accountHandle = ExtractHandle(string.Empty, description, rawHtml);
            if (string.IsNullOrWhiteSpace(accountHandle) ||
                string.Equals(accountHandle, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var likes = TryParseMetric(description, "likes");
            var comments = TryParseMetric(description, "comments");
            var shares = Math.Max(
                TryParseMetric(description, "shares"),
                TryParseMetric(description, "shared"));
            var views = Math.Max(TryParseMetric(description, "views"), likes > 0 ? likes * 8 : 0);
            var displayName = ExtractReelDisplayName(title, accountHandle);

            return new ExplorerReel(
                ExtractExternalId(normalizedUrl),
                ExtractCaption(title, description, accountHandle),
                normalizedUrl,
                ExtractMetaContent(rawHtml, "og:image"),
                TryParseReelDescriptionDate(description),
                views,
                likes,
                comments,
                shares == 0 ? Math.Max(0, likes / 40) : shares,
                new ExplorerAccountPreview(
                    accountHandle,
                    displayName,
                    string.Empty,
                    string.Empty,
                    0));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Explorer could not read reel snapshot metadata for {Url}.", normalizedUrl);
            return null;
        }
    }

    private async Task<ExplorerReel?> ReadReelAsync(IBrowserContext context, string reelUrl, CancellationToken cancellationToken)
    {
        var page = await context.NewPageAsync();

        try
        {
            var response = await page.GotoAsync(reelUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = Math.Max(5, _options.ExplorerNavigationTimeoutSeconds) * 1_000
            });
            ThrowIfInstagramRateLimited(response, reelUrl, "read reel");

            await page.WaitForTimeoutAsync(750);
            cancellationToken.ThrowIfCancellationRequested();
            await ThrowIfInstagramRateLimitedPageAsync(page, reelUrl, "read reel");

            var snapshot = await page.EvaluateAsync<ExplorerSnapshot>(
                """
                () => {
                  const text = document.body?.innerText || "";
                  const html = document.documentElement?.outerHTML || "";
                  const accountAnchor = Array.from(document.querySelectorAll("a[href]"))
                    .map((anchor) => anchor.getAttribute("href") || "")
                    .find((href) => /^\/[A-Za-z0-9._]+\/$/.test(href) && !/^\/(?:accounts|direct|explore|p|reel|reels|stories|tv)\/$/i.test(href));

                  return {
                    title: document.querySelector('meta[property="og:title"]')?.getAttribute('content') || "",
                    description: document.querySelector('meta[property="og:description"]')?.getAttribute('content') || "",
                    image: document.querySelector('meta[property="og:image"]')?.getAttribute('content') || "",
                    text,
                    html,
                    timeValue: document.querySelector('time')?.getAttribute('datetime') || "",
                    pageUrl: window.location.href,
                    accountPath: accountAnchor || ""
                  };
                }
                """);

            if (snapshot is null)
            {
                return null;
            }

            var accountHandle = ExtractHandle(snapshot.AccountPath, snapshot.Description, snapshot.Html);
            var likes = TryParseMetric(snapshot.Description, "likes");
            var comments = TryParseMetric(snapshot.Description, "comments");
            var shares = Math.Max(
                TryParseMetric(snapshot.Text, "shares"),
                TryParseMetric(snapshot.Text, "shared"));
            var views = Math.Max(TryParseMetric(snapshot.Text, "views"), likes > 0 ? likes * 8 : 0);

            return new ExplorerReel(
                ExtractExternalId(snapshot.PageUrl),
                ExtractCaption(snapshot.Title, snapshot.Description, accountHandle),
                snapshot.PageUrl,
                snapshot.Image,
                TryParsePublishedAt(snapshot.TimeValue),
                views,
                likes,
                comments,
                shares == 0 ? Math.Max(0, likes / 40) : shares,
                new ExplorerAccountPreview(
                    accountHandle,
                    accountHandle,
                    string.Empty,
                    string.Empty,
                    0));
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task<IReadOnlyCollection<ExplorerReel>> ReadRecentAccountReelsAsync(
        IBrowserContext context,
        string handle,
        ExplorerAccountPreview preview,
        int count,
        CancellationToken cancellationToken)
    {
        var page = await context.NewPageAsync();

        try
        {
            var profileUrl = $"https://www.instagram.com/{handle}/";
            var response = await page.GotoAsync(profileUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = Math.Max(5, _options.ExplorerNavigationTimeoutSeconds) * 1_000
            });
            ThrowIfInstagramRateLimited(response, profileUrl, "read profile reels");

            await page.WaitForTimeoutAsync(900);
            cancellationToken.ThrowIfCancellationRequested();
            await ThrowIfInstagramRateLimitedPageAsync(page, profileUrl, "read profile reels");

            var discoveredUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < 3 && discoveredUrls.Count < count * 2; attempt++)
            {
                foreach (var url in await ReadVisibleReelLinksAsync(page))
                {
                    discoveredUrls.Add(url);
                }

                if (discoveredUrls.Count >= count * 2)
                {
                    break;
                }

                await page.EvaluateAsync("() => window.scrollTo(0, document.body.scrollHeight)");
                await page.Mouse.WheelAsync(0, 1800);
                await page.WaitForTimeoutAsync(500);
            }

            var reels = new List<ExplorerReel>();
            foreach (var url in discoveredUrls.Take(Math.Max(count * 2, count)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var reel = await ReadReelAsync(context, url, cancellationToken);
                    if (reel is not null)
                    {
                        var account = string.IsNullOrWhiteSpace(reel.Account.ProfileImageUrl) && !string.IsNullOrWhiteSpace(preview.ProfileImageUrl)
                            ? preview
                            : reel.Account;
                        reels.Add(reel with { Account = account });
                    }
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Explorer could not read recent reel {Url} for @{Handle}.", url, handle);
                }

                if (reels.Count >= count)
                {
                    break;
                }
            }

            return reels
                .OrderByDescending(x => x.PublishedAtUtc)
                .ThenByDescending(x => x.Views)
                .Take(count)
                .ToList();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task<CachedExplorerSearch> GetOrCreateCachedSearchAsync(
        string cacheKey,
        string query,
        string sortBy,
        long? minViews,
        long? minLikes,
        long? minComments,
        long? minShares,
        int targetResults,
        int requiredResults,
        CancellationToken cancellationToken)
    {
        if (memoryCache.TryGetValue<CachedExplorerSearch>(cacheKey, out var cachedSearch) &&
            cachedSearch is not null &&
            cachedSearch.CoveredResults >= requiredResults)
        {
            return cachedSearch;
        }

        var searchLock = SearchLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await searchLock.WaitAsync(cancellationToken);

        try
        {
            if (memoryCache.TryGetValue<CachedExplorerSearch>(cacheKey, out cachedSearch) &&
                cachedSearch is not null &&
                cachedSearch.CoveredResults >= requiredResults)
            {
                return cachedSearch;
            }

            var rebuiltSearch = await BuildSearchSnapshotAsync(
                query,
                sortBy,
                minViews,
                minLikes,
                minComments,
                minShares,
                targetResults,
                cancellationToken);

            memoryCache.Set(
                cacheKey,
                rebuiltSearch,
                TimeSpan.FromMinutes(Math.Max(1, _options.ExplorerSearchCacheMinutes)));
            return rebuiltSearch;
        }
        finally
        {
            searchLock.Release();
        }
    }

    private async Task<CachedExplorerSearch> BuildSearchSnapshotAsync(
        string query,
        string sortBy,
        long? minViews,
        long? minLikes,
        long? minComments,
        long? minShares,
        int targetResults,
        CancellationToken cancellationToken)
    {
        await _searchSlots.WaitAsync(cancellationToken);

        try
        {
            var session = await ResolveExplorerSessionAsync(cancellationToken);
            var usePersistentProfile = session.HasSession;
            InstagramBrowserProfileService.PersistentBrowserLease? persistentLease = null;
            IPlaywright? playwright = null;
            IBrowser? browser = null;
            IBrowserContext? context = null;
            IPage? pageInstance = null;

            try
            {
                if (usePersistentProfile)
                {
                    persistentLease = await browserProfileService.AcquireAsync(_options.RpaHeadless, cancellationToken);
                    context = persistentLease.Context;
                    await InstagramBrowserProfileService.ExportSessionStateAsync(context, session.SessionStatePath);
                }
                else
                {
                    playwright = await Playwright.CreateAsync();
                    browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = _options.RpaHeadless,
                        ChromiumSandbox = false,
                        Args =
                        [
                            "--no-sandbox",
                            "--disable-setuid-sandbox",
                            "--disable-gpu",
                            "--disable-dev-shm-usage"
                        ]
                    });

                    context = await CreateContextAsync(browser, session);
                }

                pageInstance = await context.NewPageAsync();
                var searchUrl = $"https://www.instagram.com/explore/search/keyword/?q={Uri.EscapeDataString(query)}";

                var response = await pageInstance.GotoAsync(searchUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = Math.Max(5, _options.ExplorerNavigationTimeoutSeconds) * 1_000
                });
                ThrowIfInstagramRateLimited(response, searchUrl, "search reels");

                await pageInstance.WaitForTimeoutAsync(1_250);
                await ThrowIfInstagramRateLimitedPageAsync(pageInstance, searchUrl, "search reels");

                var discoveredUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var maxCandidates = Math.Max(targetResults * 3, 80);

                for (var attempt = 0; attempt < 8 && discoveredUrls.Count < maxCandidates; attempt++)
                {
                    foreach (var url in await ReadVisibleReelLinksAsync(pageInstance))
                    {
                        discoveredUrls.Add(url);
                    }

                    await pageInstance.EvaluateAsync(
                        """
                        () => {
                          if (document.scrollingElement) {
                            document.scrollingElement.scrollTop = document.scrollingElement.scrollHeight;
                          }
                          window.scrollTo(0, document.body.scrollHeight);
                        }
                        """);
                    await pageInstance.Mouse.WheelAsync(0, 2400);
                    await pageInstance.WaitForTimeoutAsync(600);
                }

                var reels = new ConcurrentBag<ExplorerReel>();
                await Parallel.ForEachAsync(
                    discoveredUrls,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Math.Max(1, _options.ExplorerMaxConcurrentReelLoads)
                    },
                    async (url, token) =>
                    {
                        try
                        {
                            var reel = await ReadReelAsync(context, url, token);
                            if (reel is not null)
                            {
                                reels.Add(reel);
                            }
                        }
                        catch (Exception exception)
                        {
                            logger.LogWarning(exception, "Explorer could not read reel {Url}", url);
                        }
                    });

                var filtered = reels
                    .Where(x => !minViews.HasValue || x.Views >= minViews.Value)
                    .Where(x => !minLikes.HasValue || x.Likes >= minLikes.Value)
                    .Where(x => !minComments.HasValue || x.Comments >= minComments.Value)
                    .Where(x => !minShares.HasValue || x.Shares >= minShares.Value)
                    .OrderByDescending(x => ResolveSortValue(x, sortBy))
                    .ThenByDescending(x => x.PublishedAtUtc)
                    .ToList();

                return new CachedExplorerSearch(
                    filtered,
                    Math.Max(targetResults, filtered.Count),
                    discoveredUrls.Count >= maxCandidates);
            }
            finally
            {
                if (pageInstance is not null)
                {
                    await pageInstance.CloseAsync();
                }

                if (persistentLease is not null)
                {
                    await persistentLease.DisposeAsync();
                }

                if (context is not null && !usePersistentProfile)
                {
                    await context.CloseAsync();
                }

                if (browser is not null)
                {
                    await browser.CloseAsync();
                }

                playwright?.Dispose();
            }
        }
        finally
        {
            _searchSlots.Release();
        }
    }

    private static string BuildCacheKey(
        string query,
        string sortBy,
        long? minViews,
        long? minLikes,
        long? minComments,
        long? minShares)
    {
        return string.Join(
            '|',
            query.Trim().ToLowerInvariant(),
            sortBy.Trim().ToLowerInvariant(),
            minViews?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            minLikes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            minComments?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            minShares?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static decimal ResolveSortValue(ExplorerReel reel, string sortBy)
    {
        return sortBy.Trim().ToLowerInvariant() switch
        {
            "likes" => reel.Likes,
            "comments" => reel.Comments,
            "shares" => reel.Shares,
            _ => reel.Views
        };
    }

    private static string ExtractCaption(string title, string description, string handle)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            var cleaned = title.Replace($" by {handle}", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        return string.IsNullOrWhiteSpace(description)
            ? $"Instagram reel from @{handle}"
            : description.Split(':').Last().Trim();
    }

    private static string ExtractHandle(string accountPath, string description, string html)
    {
        if (!string.IsNullOrWhiteSpace(accountPath))
        {
            var candidate = accountPath.Trim('/').ToLowerInvariant();
            if (!ReservedInstagramPaths.Contains(candidate))
            {
                return candidate;
            }
        }

        var ownerUsernameMatch = Regex.Match(
            html ?? string.Empty,
            "\"owner_username\":\"(?<handle>[A-Za-z0-9._]{2,})\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (ownerUsernameMatch.Success)
        {
            return ownerUsernameMatch.Groups["handle"].Value.ToLowerInvariant();
        }

        var ownerObjectMatch = Regex.Match(
            html ?? string.Empty,
            "\"owner\"\\s*:\\s*\\{[^{}]{0,400}?\"username\":\"(?<handle>[A-Za-z0-9._]{2,})\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (ownerObjectMatch.Success)
        {
            return ownerObjectMatch.Groups["handle"].Value.ToLowerInvariant();
        }

        var usernameMatch = Regex.Match(
            html ?? string.Empty,
            "\"username\":\"(?<handle>[A-Za-z0-9._]{2,})\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (usernameMatch.Success && !ReservedInstagramPaths.Contains(usernameMatch.Groups["handle"].Value))
        {
            return usernameMatch.Groups["handle"].Value.ToLowerInvariant();
        }

        var descriptionOwnerMatch = Regex.Match(
            description ?? string.Empty,
            @"-\s*(?<handle>[A-Za-z0-9._]{2,})\s+on\s+[^:]{3,120}:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (descriptionOwnerMatch.Success && !ReservedInstagramPaths.Contains(descriptionOwnerMatch.Groups["handle"].Value))
        {
            return descriptionOwnerMatch.Groups["handle"].Value.ToLowerInvariant();
        }

        var match = Regex.Match(description ?? string.Empty, @"@?(?<handle>[A-Za-z0-9._]{2,})");
        return match.Success ? match.Groups["handle"].Value.ToLowerInvariant() : "unknown";
    }

    private static string ExtractExternalId(string url)
    {
        var match = Regex.Match(url ?? string.Empty, @"/(?:reel|tv|p)/(?<id>[^/?#]+)/?", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["id"].Value : Guid.NewGuid().ToString("N");
    }

    private static string NormalizeInstagramReelUrl(string? reelUrl)
    {
        var normalized = reelUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("The reel URL is required.");
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Paste a valid Instagram reel URL.");
        }

        if (!uri.AbsolutePath.Contains("/reel/", StringComparison.OrdinalIgnoreCase) &&
            !uri.AbsolutePath.Contains("/reels/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The URL must point to an Instagram reel.");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
    }

    private static DateTime? TryParsePublishedAt(string value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static DateTime? TryParseReelDescriptionDate(string description)
    {
        var match = Regex.Match(
            description ?? string.Empty,
            @"\son\s+(?<date>[A-Za-z]+\s+\d{1,2},\s+\d{4})\s*:",
            RegexOptions.CultureInvariant);

        return match.Success &&
               DateTime.TryParse(
                   match.Groups["date"].Value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out var parsed)
            ? parsed
            : null;
    }

    private static string ExtractReelDisplayName(string title, string handle)
    {
        var match = Regex.Match(
            title ?? string.Empty,
            @"^(?<name>.+?)\s+on Instagram\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return handle;
        }

        var displayName = WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();
        return string.IsNullOrWhiteSpace(displayName) ? handle : displayName;
    }

    private static long TryParseFollowerCount(string description)
    {
        var match = ProfileFollowersRegex().Match(description ?? string.Empty);
        return match.Success ? ParseCompactNumber(match.Groups["followers"].Value) : 0;
    }

    private static string ExtractDisplayName(string title, string handle)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return handle;
        }

        var cleaned = title.Replace($"(@{handle})", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace($"• Instagram photos and videos", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? handle : cleaned;
    }

    private static string ExtractBio(string description, string handle)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var compact = description.Replace($"See Instagram photos and videos from {handle}", string.Empty, StringComparison.OrdinalIgnoreCase);
        return compact.Trim();
    }

    private static string ExtractMetaContent(string rawHtml, string propertyName, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            return fallback;
        }

        var propertyPattern = $@"<meta[^>]+property\s*=\s*[""']{Regex.Escape(propertyName)}[""'][^>]+content\s*=\s*[""'](?<value>.*?)[""']";
        var propertyMatch = Regex.Match(rawHtml, propertyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        if (propertyMatch.Success)
        {
            return WebUtility.HtmlDecode(propertyMatch.Groups["value"].Value);
        }

        return fallback;
    }

    private static string ExtractHtmlTitle(string rawHtml)
    {
        var match = Regex.Match(rawHtml ?? string.Empty, @"<title>(?<value>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : string.Empty;
    }

    private static long TryParseMetric(string source, string metricName)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return 0;
        }

        var match = Regex.Match(
            source,
            $@"(?<value>[\d\.,]+[kKmM]?)\s+{Regex.Escape(metricName)}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success ? ParseCompactNumber(match.Groups["value"].Value) : 0;
    }

    private static long ParseCompactNumber(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return 0;
        }

        var normalized = rawValue.Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        var multiplier = 1m;
        if (normalized.EndsWith("k", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1_000m;
            normalized = normalized[..^1];
        }
        else if (normalized.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1_000_000m;
            normalized = normalized[..^1];
        }

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? (long)Math.Round(value * multiplier, MidpointRounding.AwayFromZero)
            : 0;
    }

    private async Task<string> ReadPreviewHtmlAsync(string normalizedHandle, CancellationToken cancellationToken)
    {
        var url = $"https://www.instagram.com/{normalizedHandle}/";
        using var response = await SnapshotHttpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 429)
            {
                throw new InstagramRateLimitException(
                    "Instagram is temporarily rate-limiting reel access. Wait a few minutes or refresh the Instagram session before trying again.");
            }

            throw new InvalidOperationException($"Instagram account @{normalizedHandle} could not be read.");
        }

        return await ReadHtmlPrefixAsync(response.Content, cancellationToken);
    }

    private static async Task<string> ReadHtmlPrefixAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var buffer = new char[8_192];
        var builder = new StringBuilder(capacity: 65_536);
        const int maxChars = 65_536;

        while (builder.Length < maxChars)
        {
            var charsToRead = Math.Min(buffer.Length, maxChars - builder.Length);
            var read = await reader.ReadBlockAsync(buffer.AsMemory(0, charsToRead), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);

            if (builder.ToString().Contains("</head>", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static HttpClient CreateSnapshotHttpClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return client;
    }

    private static void ThrowIfInstagramRateLimited(IResponse? response, string url, string action)
    {
        if (response?.Status != 429)
        {
            return;
        }

        throw new InstagramRateLimitException(
            $"Instagram is temporarily rate-limiting reel access while trying to {action}. Wait a few minutes or refresh the Instagram session before trying again. Url: {url}");
    }

    private static async Task ThrowIfInstagramRateLimitedPageAsync(IPage page, string url, string action)
    {
        try
        {
            var pageText = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1_000 });
            if (pageText.Contains("HTTP ERROR 429", StringComparison.OrdinalIgnoreCase) ||
                pageText.Contains("This page isn't working", StringComparison.OrdinalIgnoreCase) &&
                pageText.Contains("429", StringComparison.OrdinalIgnoreCase))
            {
                throw new InstagramRateLimitException(
                    $"Instagram is temporarily rate-limiting reel access while trying to {action}. Wait a few minutes or refresh the Instagram session before trying again. Url: {url}");
            }
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException)
        {
        }
    }

    [GeneratedRegex(@"(?<followers>[\d\.,]+[kKmM]?)\s+Followers", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileFollowersRegex();

    private sealed record ExplorerSessionContext(bool HasSession, string SessionStatePath);

    private sealed class ExplorerSnapshot
    {
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Image { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public string Html { get; init; } = string.Empty;
        public string TimeValue { get; init; } = string.Empty;
        public string PageUrl { get; init; } = string.Empty;
        public string AccountPath { get; init; } = string.Empty;
    }

    private sealed record CachedExplorerSearch(
        IReadOnlyList<ExplorerReel> Reels,
        int CoveredResults,
        bool MayHaveMore);
}
