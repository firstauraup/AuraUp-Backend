using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Services;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class ApifyInstagramInspectionProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<InstagramIntegrationOptions> options,
    IInstagramSettingsService settingsService,
    ILogger<ApifyInstagramInspectionProvider> logger)
    : IInstagramInspectionProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly InstagramIntegrationOptions _options = options.Value;

    public string Name => "Apify";

    public async Task<InspectionPayload> InspectAccountAsync(
        InstagramInspectionRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedHandle = request.Handle.Trim().TrimStart('@').ToLowerInvariant();
        var researchPrompt = request.ResearchPrompt.Trim();
        var desiredNewPosts = request.DesiredNewPosts <= 0 ? 12 : request.DesiredNewPosts;
        var startFromPostIndex = Math.Max(0, request.StartFromPostIndex);
        var discoveryLimit = ResolveDiscoveryLimit(request, desiredNewPosts, startFromPostIndex);
        var knownIds = new HashSet<string>(request.KnownPostExternalIds, StringComparer.OrdinalIgnoreCase);
        var profileUrl = $"https://www.instagram.com/{normalizedHandle}/";

        var detailItem = await TryGetProfileDetailsAsync(profileUrl, cancellationToken);
        var discoveredPosts = await GetPostsAsync(profileUrl, discoveryLimit, cancellationToken);

        if (discoveredPosts.Count == 0 && detailItem?.LatestPosts.Count > 0)
        {
            discoveredPosts = await MapPostsAsync(detailItem.LatestPosts, cancellationToken);
        }

        if (detailItem is null && discoveredPosts.Count == 0)
        {
            throw new InvalidOperationException(
                $"Apify did not return public data for @{normalizedHandle}. Verify the handle, confirm the profile is public, and check the Apify token.");
        }

        var visiblePosts = discoveredPosts
            .OrderByDescending(x => x.PublishedAtUtc)
            .ThenByDescending(x => x.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Skip(startFromPostIndex)
            .ToList();

        var newPosts = visiblePosts
            .Where(x => !knownIds.Contains(x.ExternalId))
            .Take(desiredNewPosts)
            .ToList();

        var seenPostExternalIds = discoveredPosts
            .Select(x => x.ExternalId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var profileData = BuildProfileData(normalizedHandle, detailItem, discoveredPosts);
        var averageViews = newPosts.Count == 0 ? 0 : newPosts.Average(x => x.Views);
        var strongestPost = newPosts.OrderByDescending(x => x.Views).FirstOrDefault();

        logger.LogInformation(
            "Apify inspection completed for @{Handle}. Discovered {DiscoveredCount} posts and selected {SelectedCount} new posts.",
            normalizedHandle,
            discoveredPosts.Count,
            newPosts.Count);

        return new InspectionPayload
        {
            Handle = normalizedHandle,
            DisplayName = profileData.DisplayName,
            ProfileImageUrl = profileData.ProfileImageUrl,
            Bio = profileData.Bio,
            FollowersCount = profileData.FollowersCount,
            ResearchSummary = BuildResearchSummary(normalizedHandle, researchPrompt, seenPostExternalIds.Count, newPosts, averageViews, strongestPost),
            SeenPostExternalIds = seenPostExternalIds,
            Posts = newPosts
        };
    }

    private async Task<ApifyProfileDetailItem?> TryGetProfileDetailsAsync(string profileUrl, CancellationToken cancellationToken)
    {
        try
        {
            var items = await RunActorAsync<ApifyProfileDetailItem>(
                new ApifyActorInput(
                    [profileUrl],
                    "details",
                    null),
                cancellationToken);

            return items.FirstOrDefault();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Apify profile details could not be read for {ProfileUrl}", profileUrl);
            return null;
        }
    }

    private async Task<List<InspectedPostPayload>> GetPostsAsync(string profileUrl, int discoveryLimit, CancellationToken cancellationToken)
    {
        var items = await RunActorAsync<ApifyPostItem>(
            new ApifyActorInput(
                [profileUrl],
                "posts",
                discoveryLimit),
            cancellationToken);

        return await MapPostsAsync(items, cancellationToken);
    }

    private async Task<List<TItem>> RunActorAsync<TItem>(ApifyActorInput input, CancellationToken cancellationToken)
    {
        var settings = settingsService.Current;
        var token = ResolveApiToken(settings);
        var actorId = NormalizeText(settings.ApifyActorId, _options.ApifyActorId);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new InvalidOperationException("Instagram:ApifyActorId is required.");
        }

        var baseUrl = NormalizeText(settings.ApifyBaseUrl, _options.ApifyBaseUrl).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Instagram:ApifyBaseUrl is required.");
        }

        var requestUri = $"{baseUrl}/acts/{Uri.EscapeDataString(actorId)}/run-sync-get-dataset-items?token={Uri.EscapeDataString(token)}";
        using var client = httpClientFactory.CreateClient();
        var timeoutSeconds = settings.ApifyRequestTimeoutSeconds > 0
            ? settings.ApifyRequestTimeoutSeconds
            : _options.ApifyRequestTimeoutSeconds;
        client.Timeout = TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds));

        using var response = await client.PostAsJsonAsync(requestUri, input, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Apify request failed with status {(int)response.StatusCode}. Response: {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var items = await JsonSerializer.DeserializeAsync<List<TItem>>(stream, JsonOptions, cancellationToken);
        return items ?? [];
    }

    private static string ResolveApiToken(InstagramRuntimeSettings settings)
    {
        var token = settings.ApifyApiToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable("APIFY_API_TOKEN")
                ?? Environment.GetEnvironmentVariable("Instagram__ApifyApiToken");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Apify token was not configured. Set Instagram:ApifyApiToken or the APIFY_API_TOKEN environment variable.");
        }

        return token.Trim();
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int ResolveDiscoveryLimit(InstagramInspectionRequest request, int desiredNewPosts, int startFromPostIndex)
    {
        var requestedLimit = request.MaxDiscoveryPosts > 0
            ? request.MaxDiscoveryPosts
            : startFromPostIndex + desiredNewPosts;

        return Math.Max(desiredNewPosts, requestedLimit);
    }

    private static ProfileData BuildProfileData(
        string normalizedHandle,
        ApifyProfileDetailItem? detailItem,
        IReadOnlyCollection<InspectedPostPayload> discoveredPosts)
    {
        if (detailItem is not null)
        {
            return new ProfileData(
                string.IsNullOrWhiteSpace(detailItem.FullName) ? normalizedHandle : detailItem.FullName.Trim(),
                detailItem.ProfilePicUrlHd ?? detailItem.ProfilePicUrl ?? string.Empty,
                detailItem.Biography ?? string.Empty,
                detailItem.FollowersCount);
        }

        var fallbackCaption = discoveredPosts.FirstOrDefault()?.Caption ?? string.Empty;
        var fallbackBio = string.IsNullOrWhiteSpace(fallbackCaption)
            ? string.Empty
            : $"Profile data inferred from recent public posts for @{normalizedHandle}.";

        return new ProfileData(
            normalizedHandle,
            string.Empty,
            fallbackBio,
            0);
    }

    private static string BuildResearchSummary(
        string handle,
        string researchPrompt,
        int seenCount,
        IReadOnlyCollection<InspectedPostPayload> newPosts,
        double averageViews,
        InspectedPostPayload? strongestPost)
    {
        if (newPosts.Count == 0)
        {
            return $"Apify audit for @{handle}. No new public reels required analysis. {seenCount} public reels were already known.";
        }

        return
            $"Apify audit for @{handle}. {newPosts.Count} new public reels were analyzed and {Math.Max(0, seenCount - newPosts.Count)} were already known. Average estimated views: {averageViews:0}. Strongest new reel: {strongestPost?.Views ?? 0:n0} estimated views. Prompt focus: {researchPrompt}";
    }

    private async Task<List<InspectedPostPayload>> MapPostsAsync(
        IReadOnlyCollection<ApifyPostItem> items,
        CancellationToken cancellationToken)
    {
        var posts = new List<InspectedPostPayload>(items.Count);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var post = await MapPostAsync(item, cancellationToken);
            if (post is not null)
            {
                posts.Add(post);
            }
        }

        return posts;
    }

    private async Task<InspectedPostPayload?> MapPostAsync(ApifyPostItem source, CancellationToken cancellationToken)
    {
        if (!IsReel(source))
        {
            return null;
        }

        var externalId = ResolveExternalId(source.ShortCode, source.Id, source.Url);
        if (string.IsNullOrWhiteSpace(externalId))
        {
            return null;
        }

        var caption = source.Caption?.Trim() ?? string.Empty;
        var classification = PostTopicClassifier.Classify(caption, transcript: null);
        var views = source.VideoViewCount ?? source.VideoPlayCount ?? source.LikesCount ?? 0;
        var url = source.Url?.Trim() ?? string.Empty;
        var thumbnailUrl = string.IsNullOrWhiteSpace(source.DisplayUrl)
            ? await TryResolveThumbnailUrlAsync(url, cancellationToken)
            : source.DisplayUrl.Trim();

        return new InspectedPostPayload
        {
            IsReel = true,
            ExternalId = externalId,
            Caption = caption,
            Url = url,
            ThumbnailUrl = thumbnailUrl,
            PublishedAtUtc = source.Timestamp ?? DateTime.UtcNow,
            Views = Math.Max(0, views),
            Likes = Math.Max(0, source.LikesCount ?? 0),
            Comments = Math.Max(0, source.CommentsCount ?? 0),
            Topic = classification.Topic,
            TopicConfidence = classification.TopicConfidence,
            ContentAngle = classification.ContentAngle,
            HookStyle = classification.HookStyle,
            ThemeSummary = classification.ThemeSummary
        };
    }

    private static bool IsReel(ApifyPostItem source)
    {
        if (Domain.Entities.TrackedPost.LooksLikeReelUrl(source.Url))
        {
            return true;
        }

        var productType = (source.ProductType ?? string.Empty).Trim();
        if (productType.Equals("clips", StringComparison.OrdinalIgnoreCase) ||
            productType.Equals("reel", StringComparison.OrdinalIgnoreCase) ||
            productType.Equals("reels", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (source.IsVideo == true)
        {
            return !string.Equals((source.Type ?? string.Empty).Trim(), "image", StringComparison.OrdinalIgnoreCase);
        }

        return source.VideoViewCount.HasValue || source.VideoPlayCount.HasValue;
    }

    private async Task<string> TryResolveThumbnailUrlAsync(string reelUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reelUrl))
        {
            return string.Empty;
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Apify thumbnail fallback failed for reel {ReelUrl}", reelUrl);
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

    private static string ResolveExternalId(string? shortCode, string? id, string? url)
    {
        if (!string.IsNullOrWhiteSpace(shortCode))
        {
            return shortCode.Trim();
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            return id.Trim();
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var normalizedUrl = url.Trim().TrimEnd('/');
        var segments = normalizedUrl.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.LastOrDefault() ?? normalizedUrl;
    }

    private sealed record ProfileData(
        string DisplayName,
        string ProfileImageUrl,
        string Bio,
        long FollowersCount);

    private sealed record ApifyActorInput(
        [property: JsonPropertyName("directUrls")] IReadOnlyCollection<string> DirectUrls,
        [property: JsonPropertyName("resultsType")] string ResultsType,
        [property: JsonPropertyName("resultsLimit")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? ResultsLimit);

    private sealed record ApifyProfileDetailItem(
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("fullName")] string? FullName,
        [property: JsonPropertyName("biography")] string? Biography,
        [property: JsonPropertyName("followersCount")] long FollowersCount,
        [property: JsonPropertyName("profilePicUrl")] string? ProfilePicUrl,
        [property: JsonPropertyName("profilePicUrlHD")] string? ProfilePicUrlHd,
        [property: JsonPropertyName("latestPosts")] List<ApifyPostItem> LatestPosts);

    private sealed record ApifyPostItem(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("shortCode")] string? ShortCode,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("caption")] string? Caption,
        [property: JsonPropertyName("displayUrl")] string? DisplayUrl,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("productType")] string? ProductType,
        [property: JsonPropertyName("isVideo")] bool? IsVideo,
        [property: JsonPropertyName("timestamp")] DateTime? Timestamp,
        [property: JsonPropertyName("likesCount")] long? LikesCount,
        [property: JsonPropertyName("commentsCount")] long? CommentsCount,
        [property: JsonPropertyName("videoViewCount")] long? VideoViewCount,
        [property: JsonPropertyName("videoPlayCount")] long? VideoPlayCount);
}
