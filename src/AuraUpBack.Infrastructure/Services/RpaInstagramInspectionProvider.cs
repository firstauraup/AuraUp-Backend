using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Services;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace AuraUpBack.Infrastructure.Services;

internal sealed partial class RpaInstagramInspectionProvider(
    IInstagramConnectionAutomation instagramConnectionAutomation,
    InstagramBrowserProfileService browserProfileService,
    IOptions<InstagramIntegrationOptions> options,
    IInspectionJobQueue inspectionJobQueue,
    ILogger<RpaInstagramInspectionProvider> logger)
    : IInstagramInspectionProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient ProfileSnapshotHttpClient = CreateProfileSnapshotHttpClient();
    private readonly InstagramIntegrationOptions _options = options.Value;

    public string Name => "Rpa";

    public async Task<InspectionPayload> InspectAccountAsync(
        InstagramInspectionRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedHandle = request.Handle.Trim().TrimStart('@').ToLowerInvariant();
        var researchPrompt = request.ResearchPrompt.Trim();
        var knownIds = new HashSet<string>(request.KnownPostExternalIds, StringComparer.OrdinalIgnoreCase);
        var startFromPostIndex = Math.Max(0, request.StartFromPostIndex);
        var desiredNewPosts = ResolveDesiredNewPosts(request.DesiredNewPosts);
        var discoveryLimit = ResolveDiscoveryLimit(_options.RpaMaxPosts, request.MaxDiscoveryPosts, desiredNewPosts, startFromPostIndex);
        var sessionStatePath = ResolveSessionStatePath(_options.RpaSessionStatePath);
        var connectionState = await instagramConnectionAutomation.EnsureConnectedAsync(cancellationToken);

        if (!_options.RpaHeadless &&
            connectionState.Status != Domain.Enums.InstagramConnectionStatus.Connected)
        {
            throw new InvalidOperationException(
                "Instagram manual login is pending. Finish the browser flow from the admin before running inspections or monitoring.");
        }

        if (connectionState.Status == Domain.Enums.InstagramConnectionStatus.VerificationRequired)
        {
            throw new InvalidOperationException(
                "Instagram still requires a verification code. Complete /api/integrations/instagram/verify-code before inspecting profiles.");
        }

        var hasAuthenticatedSession = connectionState.Status == Domain.Enums.InstagramConnectionStatus.Connected && connectionState.SessionStateExists;
        var allowPublicMode = _options.AllowPublicProfileReadWithoutSession;

        if (!hasAuthenticatedSession && !allowPublicMode)
        {
            throw new InvalidOperationException(
                $"RPA session file was not found at '{sessionStatePath}' and there is no valid Instagram login. Connect Instagram first or enable public profile mode.");
        }

        logger.LogInformation("RPA inspection started for @{Handle}", normalizedHandle);
        IBrowserContext? context = null;
        InstagramBrowserProfileService.PersistentBrowserLease? persistentLease = null;
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IPage? page = null;
        var usePersistentProfile = hasAuthenticatedSession;

        try
        {
            if (usePersistentProfile)
            {
                persistentLease = await browserProfileService.AcquireAsync(_options.RpaHeadless, cancellationToken);
                context = persistentLease.Context;
                await InstagramBrowserProfileService.ExportSessionStateAsync(context, sessionStatePath);
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
                        "--disable-dev-shm-usage",
                        "--disable-features=site-per-process",
                        "--disable-background-networking",
                        "--disable-background-timer-throttling",
                        "--disable-renderer-backgrounding",
                        "--disable-backgrounding-occluded-windows"
                    ]
                });
            }

            RpaProfileData? profileData = null;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (!usePersistentProfile && context is not null)
                {
                    await TryCloseContextAsync(context);
                    context = null;
                }

                if (page is not null)
                {
                    await TryClosePageAsync(page);
                    page = null;
                }

                if (!usePersistentProfile)
                {
                    context = await CreateContextAsync(browser!, hasAuthenticatedSession, sessionStatePath);
                }

                if (context is null)
                {
                    throw new InvalidOperationException("RPA browser context was not initialized.");
                }

                page = await context.NewPageAsync();

                try
                {
                    ReportProgress(request.JobId, "Opening profile", string.Empty, 0, 0, 0);
                    page = await OpenProfileAsync(context, page, normalizedHandle, cancellationToken);

                    await page.WaitForTimeoutAsync(3_000);
                    var profileExtraction = await ExtractProfileAsync(context, page, normalizedHandle, knownIds, desiredNewPosts, discoveryLimit, request.JobId, cancellationToken);
                    page = profileExtraction.Page;
                    profileData = profileExtraction.Profile;

                    if (profileData.PostLinks.Count == 0)
                    {
                        page = await NavigateWithCrashRecoveryAsync(
                            context,
                            page,
                            $"https://www.instagram.com/{normalizedHandle}/",
                            new PageGotoOptions
                            {
                                WaitUntil = WaitUntilState.DOMContentLoaded,
                                Timeout = 60_000
                            });
                        await page.WaitForTimeoutAsync(3_000);
                        profileExtraction = await ExtractProfileAsync(context, page, normalizedHandle, knownIds, desiredNewPosts, discoveryLimit, request.JobId, cancellationToken);
                        page = profileExtraction.Page;
                        profileData = profileExtraction.Profile;

                        if (profileData.PostLinks.Count == 0)
                        {
                            page = await TryRecoverProfileTabAsync(context, page, normalizedHandle);
                            profileExtraction = await ExtractProfileAsync(context, page, normalizedHandle, knownIds, desiredNewPosts, discoveryLimit, request.JobId, cancellationToken);
                            page = profileExtraction.Page;
                            profileData = profileExtraction.Profile;
                        }
                    }

                    if (profileData.PostLinks.Count > 0)
                    {
                        break;
                    }

                    logger.LogWarning(
                        "RPA profile attempt {Attempt} for @{Handle} completed without visible posts. Retrying with a fresh context.",
                        attempt,
                        normalizedHandle);
                }
                catch (PlaywrightException exception) when (IsPageCrashException(exception))
                {
                    logger.LogWarning(
                        exception,
                        "RPA profile attempt {Attempt} for @{Handle} crashed. Retrying with a fresh context.",
                        attempt,
                        normalizedHandle);
                }
            }

            if (profileData is null || profileData.PostLinks.Count == 0)
            {
                throw new InvalidOperationException(
                    $"RPA could not stabilize Chromium while reading @{normalizedHandle}. The profile may be blocked, Instagram may be throttling the session, or the saved session may need to be refreshed.");
            }

            var visiblePostLinks = profileData.PostLinks
                .Skip(startFromPostIndex)
                .ToList();

            var candidatePostLinks = visiblePostLinks
                .Where(postUrl => !knownIds.Contains(ExtractExternalId(postUrl, normalizedHandle, 0)))
                .ToList();

            var seenExternalIds = candidatePostLinks
                .Select(postUrl => ExtractExternalId(postUrl, normalizedHandle, 0))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var activeContext = context ?? throw new InvalidOperationException("RPA browser context was not available after profile extraction.");

            var posts = new List<InspectedPostPayload>();
            foreach (var postUrl in candidatePostLinks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var externalId = ExtractExternalId(postUrl, normalizedHandle, 0);
                try
                {
                    ReportProgress(request.JobId, "Reading reel", postUrl, posts.Count, seenExternalIds.Count, posts.Count);
                    var post = await ExtractPostAsync(activeContext, postUrl, normalizedHandle, externalId, posts.Count, cancellationToken);
                    if (post is not null)
                    {
                        posts.Add(post);
                        ReportProgress(request.JobId, "Reel analyzed", post.Caption, posts.Count, seenExternalIds.Count, posts.Count);
                        if (posts.Count >= desiredNewPosts)
                        {
                            break;
                        }
                    }
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "RPA could not read post {PostUrl}", postUrl);
                }
            }

            if (posts.Count == 0 && profileData.PostLinks.Count == 0)
            {
                throw new InvalidOperationException(
                    $"RPA found the profile for @{normalizedHandle}, but could not extract any post details.");
            }

            var averageViews = posts.Count == 0 ? 0 : posts.Average(x => x.Views);
            var strongestPost = posts.OrderByDescending(x => x.Views).FirstOrDefault();
            var failedCandidateCount = Math.Max(0, seenExternalIds.Count - posts.Count);

            return new InspectionPayload
            {
                Handle = normalizedHandle,
                DisplayName = profileData.DisplayName,
                ProfileImageUrl = profileData.ProfileImageUrl,
                Bio = profileData.Bio,
                FollowersCount = profileData.FollowersCount,
                ResearchSummary =
                    posts.Count == 0
                        ? $"RPA audit for @{normalizedHandle}. No new reels required analysis."
                        : failedCandidateCount == 0
                            ? $"RPA audit for @{normalizedHandle}. {posts.Count} new reels were analyzed. Average estimated views: {averageViews:0}. Strongest new reel: {strongestPost?.Views ?? 0:n0} views. Prompt focus: {researchPrompt}"
                            : $"RPA audit for @{normalizedHandle}. {posts.Count} new reels were analyzed and {failedCandidateCount} candidate reels could not be opened in this pass. Average estimated views: {averageViews:0}. Strongest new reel: {strongestPost?.Views ?? 0:n0} views. Prompt focus: {researchPrompt}",
                SeenPostExternalIds = seenExternalIds,
                Posts = posts
            };
        }
        catch (InvalidOperationException exception) when (!request.ReconnectRetryAttempted && IsSessionInterruptionException(exception))
        {
            if (!_options.RpaHeadless)
            {
                throw new InvalidOperationException(
                    "Instagram session was interrupted while scraping. Finish the manual browser login from the admin before trying again.");
            }

            logger.LogWarning(
                exception,
                "RPA session was interrupted while inspecting @{Handle}. Reconnecting and retrying once.",
                normalizedHandle);

            var reconnectState = await instagramConnectionAutomation.ReconnectAsync(cancellationToken);
            if (reconnectState.Status == Domain.Enums.InstagramConnectionStatus.VerificationRequired)
            {
                throw new InvalidOperationException(
                    "Instagram asked for verification or captcha again while reconnecting. Complete the verification flow before inspecting profiles.");
            }

            if (reconnectState.Status != Domain.Enums.InstagramConnectionStatus.Connected || !reconnectState.SessionStateExists)
            {
                throw new InvalidOperationException(
                    $"Instagram session was interrupted while scraping @{normalizedHandle}, and automatic reconnect did not restore a valid session.");
            }

            return await InspectAccountAsync(
                new InstagramInspectionRequest
                {
                    Handle = request.Handle,
                    ResearchPrompt = request.ResearchPrompt,
                    KnownPostExternalIds = request.KnownPostExternalIds,
                    StartFromPostIndex = request.StartFromPostIndex,
                    DesiredNewPosts = request.DesiredNewPosts,
                    MaxDiscoveryPosts = request.MaxDiscoveryPosts,
                    JobId = request.JobId,
                    ReconnectRetryAttempted = true
                },
                cancellationToken);
        }
        finally
        {
            if (page is not null)
            {
                await TryClosePageAsync(page);
            }

            if (!usePersistentProfile && context is not null)
            {
                await TryCloseContextAsync(context);
            }

            if (persistentLease is not null)
            {
                await persistentLease.DisposeAsync();
            }

            if (browser is not null)
            {
                await browser.CloseAsync();
            }

            playwright?.Dispose();
        }
    }

    private async Task<IPage> OpenProfileAsync(
        IBrowserContext context,
        IPage page,
        string handle,
        CancellationToken cancellationToken)
    {
        const int navigationTimeoutMs = 60_000;
        var reelsUrl = $"https://www.instagram.com/{handle}/reels/";

        page = await NavigateWithCrashRecoveryAsync(context, page, reelsUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = navigationTimeoutMs
        });
        await EnsureStillAuthenticatedAsync(page);

        await page.WaitForTimeoutAsync(2_000);
        page = await ExecutePageActionWithRecoveryAsync(
            context,
            page,
            reelsUrl,
            TryDismissProfilePromptsAsync,
            "dismiss profile prompts");

        return page;
    }

    private async Task<IPage> TryRecoverProfileTabAsync(
        IBrowserContext context,
        IPage page,
        string handle)
    {
        var profileUrl = $"https://www.instagram.com/{handle}/";

        try
        {
            return await ExecutePageActionWithRecoveryAsync(
                context,
                page,
                profileUrl,
                TryDismissProfilePromptsAsync,
                "dismiss profile prompts");
        }
        catch (PlaywrightException exception) when (IsPageCrashException(exception))
        {
            logger.LogWarning(
                exception,
                "RPA could not stabilize the profile prompts for @{Handle}. Continuing with direct reels navigation.",
                handle);
        }

        return await ExecutePageActionWithRecoveryAsync(
            context,
            page,
            profileUrl,
            currentPage => TryOpenVideoTabAsync(currentPage, handle),
            "open reels tab");
    }

    private async Task<IPage> NavigateWithCrashRecoveryAsync(
        IBrowserContext context,
        IPage page,
        string url,
        PageGotoOptions options)
    {
        try
        {
            await page.GotoAsync(url, options);
            return page;
        }
        catch (PlaywrightException exception) when (IsPageCrashException(exception))
        {
            logger.LogWarning(exception, "RPA page crashed navigating to {Url}. Recreating the page.", url);

            await TryClosePageAsync(page);
            var recoveredPage = await context.NewPageAsync();
            await recoveredPage.GotoAsync(url, options);
            return recoveredPage;
        }
    }

    private async Task<IPage> ExecutePageActionWithRecoveryAsync(
        IBrowserContext context,
        IPage page,
        string url,
        Func<IPage, Task> action,
        string actionName)
    {
        try
        {
            await action(page);
            return page;
        }
        catch (PlaywrightException exception) when (IsPageCrashException(exception))
        {
            logger.LogWarning(exception, "RPA page crashed during {ActionName} on {Url}. Recreating the page.", actionName, url);

            await TryClosePageAsync(page);
            var recoveredPage = await context.NewPageAsync();
            await recoveredPage.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });
            await recoveredPage.WaitForTimeoutAsync(2_000);
            await action(recoveredPage);
            return recoveredPage;
        }
    }

    private async Task<RpaProfileExtractionResult> ExtractProfileAsync(
        IBrowserContext context,
        IPage page,
        string handle,
        IReadOnlyCollection<string> knownPostExternalIds,
        int desiredNewPosts,
        int maxPostsToCollect,
        Guid? jobId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var discoveredLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownIds = new HashSet<string>(knownPostExternalIds, StringComparer.OrdinalIgnoreCase);
        RpaProfileSnapshot? latestSnapshot = null;
        var scrollPasses = 0;
        var scrollAttemptsWithoutNewPosts = 0;
        var profileUrl = $"https://www.instagram.com/{handle}/reels/";

        while (scrollAttemptsWithoutNewPosts < 8 && discoveredLinks.Count < maxPostsToCollect)
        {
            try
            {
                await EnsureStillAuthenticatedAsync(page);
                var previousCount = discoveredLinks.Count;
                latestSnapshot = await ReadProfileSnapshotAsync(page);

                if (latestSnapshot is not null)
                {
                    foreach (var postLink in latestSnapshot.PostLinks ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(postLink))
                        {
                            continue;
                        }

                        discoveredLinks.Add(postLink);
                    }
                }

                foreach (var postLink in await ReadVisiblePostLinksAsync(page))
                {
                    if (!string.IsNullOrWhiteSpace(postLink))
                    {
                        discoveredLinks.Add(postLink);
                    }
                }

                ReportProgress(
                    jobId,
                    "Discovering reels",
                    $"Found {discoveredLinks.Select(link => ExtractExternalId(link, handle, 0)).Count(externalId => !knownIds.Contains(externalId))} new candidate reels",
                    0,
                    discoveredLinks.Select(link => ExtractExternalId(link, handle, 0)).Count(externalId => !knownIds.Contains(externalId)),
                    discoveredLinks.Select(link => ExtractExternalId(link, handle, 0)).Count(externalId => !knownIds.Contains(externalId)));

                if (discoveredLinks.Count >= maxPostsToCollect)
                {
                    break;
                }

                var newDiscoveredPosts = discoveredLinks
                    .Select(link => ExtractExternalId(link, handle, 0))
                    .Count(externalId => !knownIds.Contains(externalId));

                if (newDiscoveredPosts >= desiredNewPosts && scrollPasses > 0)
                {
                    break;
                }

                if (discoveredLinks.Count == previousCount)
                {
                    scrollAttemptsWithoutNewPosts++;
                }
                else
                {
                    scrollAttemptsWithoutNewPosts = 0;
                }

                await ScrollProfileAsync(page);
                scrollPasses++;
                await page.WaitForTimeoutAsync(scrollPasses <= 2 ? 2_500 : 1_500);
            }
            catch (PlaywrightException exception) when (IsPageCrashException(exception))
            {
                logger.LogWarning(exception, "RPA page crashed extracting profile for @{Handle}. Recreating the page.", handle);
                ReportProgress(jobId, "Recovering profile", $"Chromium crashed while reading @{handle}", 0, discoveredLinks.Count, discoveredLinks.Count);

                var fallbackSnapshot = await TryReadProfileSnapshotViaHttpAsync(handle);
                if (fallbackSnapshot is not null && fallbackSnapshot.PostLinks.Count > 0)
                {
                    latestSnapshot = fallbackSnapshot;
                    foreach (var postLink in fallbackSnapshot.PostLinks)
                    {
                        if (!string.IsNullOrWhiteSpace(postLink))
                        {
                            discoveredLinks.Add(postLink);
                        }
                    }

                    ReportProgress(
                        jobId,
                        "Recovered profile",
                        $"Recovered {discoveredLinks.Count} reels for @{handle} without Chromium",
                        0,
                        discoveredLinks.Count,
                        discoveredLinks.Select(link => ExtractExternalId(link, handle, 0)).Count(externalId => !knownIds.Contains(externalId)));
                    break;
                }

                page = await NavigateWithCrashRecoveryAsync(
                    context,
                    page,
                    profileUrl,
                    new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 60_000
                    });
                await page.WaitForTimeoutAsync(2_000);
            }
        }

        if (latestSnapshot is null)
        {
            throw new InvalidOperationException("RPA could not read the Instagram profile snapshot.");
        }

        var displayName = ExtractDisplayName(latestSnapshot.Title, handle);
        var followersCount = ParseCompactNumber(ProfileFollowersRegex().Match(latestSnapshot.Description).Groups["followers"].Value);
        var bio = latestSnapshot.Candidates
            .FirstOrDefault(x => x.Length > 20 && !x.Contains("followers", StringComparison.OrdinalIgnoreCase))
            ?? $"Instagram account snapshot for @{handle}";

        var postLinks = discoveredLinks.ToList();
        if (postLinks.Count == 0)
        {
            var rawHtml = await page.ContentAsync();
            postLinks = ExtractPostUrlsFromMarkup(rawHtml)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return new RpaProfileExtractionResult(
            page,
            new RpaProfileData(
                displayName,
                latestSnapshot.ImageUrl,
                bio,
                followersCount,
                postLinks));
    }

    private static async Task<RpaProfileSnapshot?> ReadProfileSnapshotAsync(IPage page)
    {
        var rawHtml = await page.ContentAsync();
        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            return null;
        }

        return new RpaProfileSnapshot
        {
            Description = ExtractMetaContent(rawHtml, "og:description"),
            ImageUrl = ExtractMetaContent(rawHtml, "og:image"),
            Title = ExtractMetaContent(rawHtml, "og:title", ExtractHtmlTitle(rawHtml)),
            Candidates = [],
            PostLinks = ExtractPostUrlsFromMarkup(rawHtml)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static async Task<IReadOnlyCollection<string>> ReadVisiblePostLinksAsync(IPage page)
    {
        var urls = await page.EvaluateAsync<string[]>(
            """
            () => {
              const anchors = Array.from(document.querySelectorAll('a[href]'));
              return anchors
                .map((anchor) => anchor.href || "")
                .filter((href) => /\/(reel|tv|p)\//i.test(href));
            }
            """);

        return urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task ScrollProfileAsync(IPage page)
    {
        await page.EvaluateAsync(
            """
            () => {
              const reelAnchors = Array.from(document.querySelectorAll('a[href*="/reel/"], a[href*="/p/"], a[href*="/tv/"]'));
              const lastAnchor = reelAnchors.at(-1);
              if (lastAnchor instanceof HTMLElement) {
                lastAnchor.scrollIntoView({ behavior: 'instant', block: 'end' });
              }

              const selectors = ['main', 'section', 'article', 'div'];
              const candidates = [];

              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const style = window.getComputedStyle(element);
                  const canScroll = /(auto|scroll)/i.test(style.overflowY || '') && element.scrollHeight > element.clientHeight + 120;
                  if (canScroll) {
                    candidates.push(element);
                  }
                }
              }

              const uniqueCandidates = Array.from(new Set(candidates));
              for (const element of uniqueCandidates) {
                element.scrollTop = Math.max(element.scrollTop, element.scrollHeight - Math.max(200, element.clientHeight / 3));
              }

              if (document.scrollingElement) {
                document.scrollingElement.scrollTop = document.scrollingElement.scrollHeight;
              }

              window.scrollTo(0, document.body.scrollHeight);

              const loadMoreButton = Array.from(document.querySelectorAll('button'))
                .find((button) => /show more|load more|see more|ver más|mostrar más/i.test(button.textContent || ''));

              if (loadMoreButton instanceof HTMLElement) {
                loadMoreButton.click();
              }
            }
            """);

        await page.Mouse.WheelAsync(0, 1800);
        await page.Mouse.WheelAsync(0, 2200);
        await page.Keyboard.PressAsync("End");
    }

    private static async Task<RpaProfileSnapshot?> TryReadProfileSnapshotViaHttpAsync(string handle)
    {
        foreach (var url in new[]
                 {
                     $"https://www.instagram.com/{handle}/reels/",
                     $"https://www.instagram.com/{handle}/"
                 })
        {
            try
            {
                using var response = await ProfileSnapshotHttpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var rawHtml = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(rawHtml))
                {
                    continue;
                }

                var snapshot = new RpaProfileSnapshot
                {
                    Description = ExtractMetaContent(rawHtml, "og:description"),
                    ImageUrl = ExtractMetaContent(rawHtml, "og:image"),
                    Title = ExtractMetaContent(rawHtml, "og:title", ExtractHtmlTitle(rawHtml)),
                    Candidates = [],
                    PostLinks = ExtractPostUrlsFromMarkup(rawHtml)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                if (snapshot.PostLinks.Count > 0 || !string.IsNullOrWhiteSpace(snapshot.Title))
                {
                    return snapshot;
                }
            }
            catch
            {
                // Ignore HTTP fallback failures and continue trying other sources.
            }
        }

        return null;
    }

    private static IReadOnlyCollection<string> ExtractPostUrlsFromMarkup(string rawHtml)
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            return Array.Empty<string>();
        }

        var urls = PostUrlRegex()
            .Matches(rawHtml)
            .Select(match => $"https://www.instagram.com/{match.Groups["type"].Value}/{match.Groups["id"].Value}/")
            .ToList();

        urls.AddRange(
            EscapedPostUrlRegex()
                .Matches(rawHtml)
                .Select(match => $"https://www.instagram.com/{match.Groups["type"].Value}/{match.Groups["id"].Value}/"));

        return urls;
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

        var namePattern = $@"<meta[^>]+content\s*=\s*[""'](?<value>.*?)[""'][^>]+property\s*=\s*[""']{Regex.Escape(propertyName)}[""']";
        var nameMatch = Regex.Match(rawHtml, namePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return nameMatch.Success
            ? WebUtility.HtmlDecode(nameMatch.Groups["value"].Value)
            : fallback;
    }

    private static string ExtractHtmlTitle(string rawHtml)
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            return string.Empty;
        }

        var match = Regex.Match(rawHtml, @"<title>(?<value>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : string.Empty;
    }

    private static HttpClient CreateProfileSnapshotHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return client;
    }

    private static async Task TryDismissProfilePromptsAsync(IPage page)
    {
        var buttonNames = new[]
        {
            "Not Now",
            "Ahora no",
            "Cancel",
            "Cerrar",
            "Close"
        };

        foreach (var buttonName in buttonNames)
        {
            var button = page.GetByRole(AriaRole.Button, new() { Name = buttonName });
            if (await button.CountAsync() > 0)
            {
                await button.First.ClickAsync();
                await page.WaitForTimeoutAsync(500);
                return;
            }
        }
    }

    private static async Task TryOpenVideoTabAsync(IPage page, string handle)
    {
        var selectors = new[]
        {
            $"a[href='/{handle}/reels/']",
            $"a[href='https://www.instagram.com/{handle}/reels/']",
            "a[href$='/reels/']",
            "a:has-text('Reels')",
            "a:has-text('Videos')"
        };

        foreach (var selector in selectors)
        {
            var tab = page.Locator(selector);
            if (await tab.CountAsync() > 0)
            {
                await tab.First.ClickAsync();
                await page.WaitForTimeoutAsync(2_000);
                return;
            }
        }

        await page.GotoAsync($"https://www.instagram.com/{handle}/reels/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000
        });
        await page.WaitForTimeoutAsync(2_000);
    }

    private async Task<InspectedPostPayload?> ExtractPostAsync(
        IBrowserContext context,
        string postUrl,
        string handle,
        string externalId,
        int index,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Exception? lastCrashException = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var page = await context.NewPageAsync();

            try
            {
                await page.GotoAsync(postUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000
                });
                await EnsureStillAuthenticatedAsync(page);

                await page.WaitForTimeoutAsync(1_500);

                var snapshotJson = await page.EvaluateAsync<string>(
                    """
                    () => {
                      const ogTitle = document.querySelector('meta[property="og:title"]')?.getAttribute('content') || "";
                      const ogDescription = document.querySelector('meta[property="og:description"]')?.getAttribute('content') || "";
                      const ogType = document.querySelector('meta[property="og:type"]')?.getAttribute('content') || "";
                      const ogImage = document.querySelector('meta[property="og:image"]')?.getAttribute('content') || "";
                      const bodyText = document.body?.innerText || "";
                      const timeValue = document.querySelector('time')?.getAttribute('datetime') || "";
                      const hasVideoElement = !!document.querySelector('video');
                      const hasPlayButton = !!document.querySelector("svg[aria-label='Play'], svg[aria-label='Reproducir']");
                      const pageUrl = window.location.href;

                      return JSON.stringify({
                        ogTitle,
                        ogDescription,
                        ogType,
                        ogImage,
                        bodyText,
                        timeValue,
                        hasVideoElement,
                        hasPlayButton,
                        pageUrl
                      });
                    }
                    """);

                var snapshot = string.IsNullOrWhiteSpace(snapshotJson)
                    ? null
                    : JsonSerializer.Deserialize<RpaPostSnapshot>(snapshotJson, JsonOptions);

                if (snapshot is null)
                {
                    throw new InvalidOperationException($"RPA could not read the post snapshot for '{postUrl}'.");
                }

                if (!IsVideoPost(snapshot, postUrl))
                {
                    return null;
                }

                var canonicalUrl = NormalizeInstagramPostUrl(snapshot.PageUrl, postUrl, externalId);
                var caption = ExtractCaption(snapshot.OgTitle, snapshot.OgDescription, handle, index);
                var classification = PostTopicClassifier.Classify(caption, snapshot.BodyText);
                var likes = TryParseMetric(snapshot.OgDescription, "likes");
                var comments = TryParseMetric(snapshot.OgDescription, "comments");
                var shares = Math.Max(
                    TryParseMetric(snapshot.BodyText, "shares"),
                    TryParseMetric(snapshot.BodyText, "shared"));
                var views = Math.Max(TryParseMetric(snapshot.BodyText, "views"), likes > 0 ? likes * 8 : 0);
                if (views == 0)
                {
                    views = Math.Max(3_000, likes * 10 + 2_500);
                }

                if (comments == 0)
                {
                    comments = Math.Max(18, likes / 18);
                }

                var publishedAtUtc = DateTime.TryParse(snapshot.TimeValue, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedDate)
                    ? parsedDate
                    : DateTime.UtcNow.AddDays(-(index + 1) * 3);

                return new InspectedPostPayload
                {
                    IsReel = true,
                    ExternalId = externalId,
                    Caption = caption,
                    Url = canonicalUrl,
                    ThumbnailUrl = snapshot.OgImage,
                    PublishedAtUtc = publishedAtUtc,
                    Views = views,
                    Likes = Math.Max(likes, 100),
                    Comments = comments,
                    Shares = shares == 0 ? Math.Max(0, likes / 40) : shares,
                    Topic = classification.Topic,
                    TopicConfidence = classification.TopicConfidence,
                    ContentAngle = classification.ContentAngle,
                    HookStyle = classification.HookStyle,
                    ThemeSummary = classification.ThemeSummary
                };
            }
            catch (PlaywrightException exception) when (IsPageCrashException(exception))
            {
                lastCrashException = exception;
                logger.LogWarning(exception, "RPA page crashed reading post {PostUrl} on attempt {Attempt}.", postUrl, attempt);
            }
            finally
            {
                await TryClosePageAsync(page);
            }
        }

        if (lastCrashException is not null)
        {
            throw lastCrashException;
        }

        throw new InvalidOperationException($"RPA could not read the post snapshot for '{postUrl}'.");
    }

    private static bool IsPageCrashException(PlaywrightException exception)
    {
        return exception.Message.Contains("Page crashed", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Target crashed", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("RESULT_CODE_KILLED_BAD_MESSAGE", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Aw, Snap", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSessionInterruptionException(InvalidOperationException exception)
    {
        return exception.Message.Contains("redirected the session to login", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("returned the login screen while scraping", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task EnsureStillAuthenticatedAsync(IPage page)
    {
        var currentUrl = page.Url;
        if (currentUrl.Contains("/accounts/login", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Instagram redirected the session to login while scraping. Current URL: {currentUrl}");
        }

        if (currentUrl.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
            currentUrl.Contains("two_factor", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Instagram requested verification again while scraping. Current URL: {currentUrl}");
        }

        var bodyText = await page.Locator("body").InnerTextAsync();
        if (ContainsCaptchaOrVerificationSignals(bodyText))
        {
            throw new InvalidOperationException("Instagram requested verification or captcha while scraping the profile.");
        }

        if (bodyText.Contains("Log in", StringComparison.OrdinalIgnoreCase) ||
            bodyText.Contains("Iniciar sesión", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Instagram returned the login screen while scraping the profile.");
        }
    }

    private static bool ContainsCaptchaOrVerificationSignals(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            return false;
        }

        var signals = new[]
        {
            "captcha",
            "recaptcha",
            "security code",
            "código de seguridad",
            "enter the code",
            "enter confirmation code",
            "choose a way to confirm it's you",
            "suspicious login attempt",
            "confirm it's you",
            "we need to make sure it's you",
            "help us confirm you own this account"
        };

        return signals.Any(signal => bodyText.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task TryClosePageAsync(IPage page)
    {
        try
        {
            await page.CloseAsync();
        }
        catch (PlaywrightException)
        {
        }
    }

    private async Task<IBrowserContext> CreateContextAsync(IBrowser browser, bool hasAuthenticatedSession, string sessionStatePath)
    {
        var contextOptions = new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1280,
                Height = 900
            },
            Locale = "en-US",
            TimezoneId = "America/New_York"
        };

        if (hasAuthenticatedSession && File.Exists(sessionStatePath))
        {
            contextOptions.StorageStatePath = sessionStatePath;
        }

        return await browser.NewContextAsync(contextOptions);
    }

    private static async Task TryCloseContextAsync(IBrowserContext context)
    {
        try
        {
            await context.CloseAsync();
        }
        catch (PlaywrightException)
        {
        }
    }

    private static string ResolveSessionStatePath(string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
    }

    private static int ResolveDiscoveryLimit(int configuredLimit, int requestedLimit, int desiredNewPosts, int startFromPostIndex)
    {
        var minimumRequired = Math.Max(startFromPostIndex + desiredNewPosts, desiredNewPosts);

        if (requestedLimit > 0)
        {
            return Math.Max(requestedLimit, minimumRequired);
        }

        return configuredLimit <= 0 ? Math.Max(24, minimumRequired * 2) : Math.Max(configuredLimit, minimumRequired);
    }

    private static int ResolveDesiredNewPosts(int requestedLimit)
    {
        return requestedLimit <= 0 ? 12 : requestedLimit;
    }

    private void ReportProgress(Guid? jobId, string phase, string currentItem, int processedPosts, int discoveredPosts, int newPostsFound)
    {
        if (!jobId.HasValue)
        {
            return;
        }

        inspectionJobQueue.MarkProgress(
            jobId.Value,
            new InspectionJobProgress(
                phase,
                currentItem,
                processedPosts,
                discoveredPosts,
                newPostsFound));
    }

    private static string ExtractDisplayName(string ogTitle, string handle)
    {
        var cleanTitle = ogTitle.Replace("Instagram photos and videos", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim(' ', '-', '·');

        return string.IsNullOrWhiteSpace(cleanTitle) ? handle : cleanTitle;
    }

    private static string ExtractCaption(string ogTitle, string ogDescription, string handle, int index)
    {
        var quotedCaption = QuotedCaptionRegex().Match(ogTitle);
        if (quotedCaption.Success)
        {
            return quotedCaption.Groups["caption"].Value.Trim();
        }

        var descriptionCaption = QuotedCaptionRegex().Match(ogDescription);
        if (descriptionCaption.Success)
        {
            return descriptionCaption.Groups["caption"].Value.Trim();
        }

        var colonCaption = DescriptionCaptionRegex().Match(ogDescription);
        if (colonCaption.Success)
        {
            return colonCaption.Groups["caption"].Value.Trim();
        }

        var titleCaption = TitleCaptionRegex().Match(ogTitle);
        if (titleCaption.Success)
        {
            return titleCaption.Groups["caption"].Value.Trim();
        }

        return $"Instagram post {index + 1} captured via RPA for @{handle}.";
    }

    private static long TryParseMetric(string source, string metricName)
    {
        var match = Regex.Match(
            source,
            $@"(?<value>[\d\.,]+[kKmM]?)\s+{metricName}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success ? ParseCompactNumber(match.Groups["value"].Value) : 0;
    }

    private static long ParseCompactNumber(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return 0;
        }

        var normalized = rawValue.Trim().Replace(",", string.Empty);

        if (normalized.Length > 1 &&
            char.ToUpperInvariant(normalized[^1]) == 'K' &&
            decimal.TryParse(normalized[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var thousands))
        {
            return (long)(thousands * 1_000);
        }

        if (normalized.Length > 1 &&
            char.ToUpperInvariant(normalized[^1]) == 'M' &&
            decimal.TryParse(normalized[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var millions))
        {
            return (long)(millions * 1_000_000);
        }

        return long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exact)
            ? exact
            : 0;
    }

    private static string ExtractExternalId(string postUrl, string handle, int index)
    {
        var segments = postUrl.TrimEnd('/').Split('/');
        return segments.LastOrDefault() ?? $"{handle}-post-{index + 1:00}";
    }

    private static string NormalizeInstagramPostUrl(string? pageUrl, string fallbackUrl, string externalId)
    {
        var preferredUrl = string.IsNullOrWhiteSpace(pageUrl) ? fallbackUrl : pageUrl;
        if (!Uri.TryCreate(preferredUrl, UriKind.Absolute, out var uri))
        {
            return fallbackUrl;
        }

        var match = PostUrlRegex().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            return fallbackUrl;
        }

        var type = match.Groups["type"].Value.ToLowerInvariant();
        var id = match.Groups["id"].Value;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = externalId;
        }

        return $"https://www.instagram.com/{type}/{id}/";
    }

    [GeneratedRegex(@"(?<followers>[\d\.,]+[kKmM]?)\s+Followers", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileFollowersRegex();

    [GeneratedRegex("“(?<caption>.+?)”", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedCaptionRegex();

    [GeneratedRegex(@":\s*(?<caption>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DescriptionCaptionRegex();

    [GeneratedRegex(@"on Instagram:\s*(?<caption>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleCaptionRegex();

    [GeneratedRegex(@"/(?<type>reel|tv|p)/(?<id>[A-Za-z0-9_-]+)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PostUrlRegex();

    [GeneratedRegex(@"\\u002F(?<type>reel|tv|p)\\u002F(?<id>[A-Za-z0-9_-]+)\\u002F", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EscapedPostUrlRegex();

    private static bool IsVideoPost(RpaPostSnapshot snapshot, string postUrl)
    {
        if (postUrl.Contains("/reel/", StringComparison.OrdinalIgnoreCase) ||
            postUrl.Contains("/tv/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (snapshot.PageUrl.Contains("/reel/", StringComparison.OrdinalIgnoreCase) ||
            snapshot.PageUrl.Contains("/tv/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (snapshot.HasVideoElement || snapshot.HasPlayButton)
        {
            return true;
        }

        return snapshot.OgType.Contains("video", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RpaProfileData(
        string DisplayName,
        string ProfileImageUrl,
        string Bio,
        long FollowersCount,
        IReadOnlyCollection<string> PostLinks);

    private sealed record RpaProfileExtractionResult(
        IPage Page,
        RpaProfileData Profile);

    private sealed class RpaProfileSnapshot
    {
        public string Description { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string BodyText { get; set; } = string.Empty;
        public List<string> Candidates { get; set; } = [];
        public List<string> PostLinks { get; set; } = [];
    }

    private sealed class RpaPostSnapshot
    {
        public string OgTitle { get; set; } = string.Empty;
        public string OgDescription { get; set; } = string.Empty;
        public string OgType { get; set; } = string.Empty;
        public string OgImage { get; set; } = string.Empty;
        public string BodyText { get; set; } = string.Empty;
        public string TimeValue { get; set; } = string.Empty;
        public bool HasVideoElement { get; set; }
        public bool HasPlayButton { get; set; }
        public string PageUrl { get; set; } = string.Empty;
    }
}
