using AuraUpBack.Application;
using AuraUpBack.Application.Abstractions;
using AuraUpBack.Application.Commands.CreateExplorationRequest;
using AuraUpBack.Application.Commands.ConnectInstagramIntegration;
using AuraUpBack.Application.Commands.BackfillTrackedAccountHistory;
using AuraUpBack.Application.Commands.DeleteTrackedAccount;
using AuraUpBack.Application.Commands.InspectTrackedAccount;
using AuraUpBack.Application.Commands.RegisterTrackedAccount;
using AuraUpBack.Application.Commands.ReconnectInstagramIntegration;
using AuraUpBack.Application.Commands.RunExplorationRequest;
using AuraUpBack.Application.Commands.TranscribeTrackedPost;
using AuraUpBack.Application.Commands.UpdateTrackedAccountMonitoring;
using AuraUpBack.Application.Commands.VerifyInstagramIntegrationCode;
using AuraUpBack.Application.Queries.GetInstagramIntegrationStatus;
using AuraUpBack.Application.Queries.GetInstagramExplorerAccountPreview;
using AuraUpBack.Application.Queries.GetTrackedAccountAnalysis;
using AuraUpBack.Application.Queries.GetTrackedAccountOverview;
using AuraUpBack.Application.Queries.GetWatchlistDashboard;
using AuraUpBack.Application.Queries.SearchInstagramExplorer;
using AuraUpBack.Api.Auth;
using AuraUpBack.Api.Realtime;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using AuraUpBack.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Playwright;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var allowedCorsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?.Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Select(origin => origin.Trim().TrimEnd('/'))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray()
    ?? ["https://www.auraup.org", "https://auraup.org"];

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, enableMonitoringService: true);
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection(AdminAuthOptions.SectionName));
builder.Services.AddSingleton<AdminSessionService>();
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AuraUpFront", policy =>
    {
        policy
            .WithOrigins(allowedCorsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var mediaCacheRoot = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "MediaCache");
var enableThumbnailCaptureFallback = builder.Configuration.GetValue<bool?>("Media:EnableThumbnailCaptureFallback")
    ?? builder.Environment.IsDevelopment();
var thumbnailCaptureGate = new SemaphoreSlim(1, 1);

var app = builder.Build();
var inspectionJobQueue = app.Services.GetRequiredService<IInspectionJobQueue>();
var hubContext = app.Services.GetRequiredService<IHubContext<AdminEventsHub>>();

inspectionJobQueue.StatusChanged += status =>
{
    _ = BroadcastInspectionStatusAsync(hubContext, status);
};

app.UseCors("AuraUpFront");
app.UseMiddleware<AdminAuthMiddleware>();

app.MapGet("/", () => Results.Ok(new
{
    service = "AuraUpBack",
    status = "running",
    mode = "mvp"
}));

app.MapHub<AdminEventsHub>("/hubs/admin-events");

app.MapGet("/media/accounts/{accountId:guid}/posts/{postId:guid}/thumbnail", async (
    Guid accountId,
    Guid postId,
    ITrackedAccountRepository trackedAccountRepository,
    IHttpClientFactory httpClientFactory,
    IHubContext<AdminEventsHub> mediaHubContext,
    CancellationToken cancellationToken) =>
{
    var account = await trackedAccountRepository.GetByIdAsync(accountId, cancellationToken);
    var post = account?.Posts.FirstOrDefault(x => x.Id == postId);
    if (post is null)
    {
        return Results.NotFound();
    }

    var sourceUrl = post.ThumbnailUrl;
    if (string.IsNullOrWhiteSpace(sourceUrl))
    {
        sourceUrl = await TryResolveThumbnailFromReelAsync(post.Url, httpClientFactory, cancellationToken);
        if (!string.IsNullOrWhiteSpace(sourceUrl) && account is not null)
        {
            post.ThumbnailUrl = sourceUrl;
            await trackedAccountRepository.UpsertAsync(account, cancellationToken);
        }
    }

    if (string.IsNullOrWhiteSpace(sourceUrl))
    {
        return Results.NotFound();
    }

    var thumbnailCacheDirectory = Path.Combine(mediaCacheRoot, "accounts", accountId.ToString("N"), "posts", postId.ToString("N"));
    var cachedThumbnail = await TryReadCachedImageAsync(thumbnailCacheDirectory, sourceUrl, cancellationToken);
    if (cachedThumbnail is not null)
    {
        return Results.File(cachedThumbnail.Value.Bytes, cachedThumbnail.Value.ContentType);
    }

    var downloadedImage = await TryDownloadImageAsync(sourceUrl, httpClientFactory, cancellationToken);
    if (downloadedImage is not null)
    {
        await SaveCachedImageAsync(thumbnailCacheDirectory, sourceUrl, downloadedImage.Value, cancellationToken);
        await BroadcastMediaReadyAsync(mediaHubContext, accountId, postId, "thumbnail", cancellationToken);
        return Results.File(downloadedImage.Value.Bytes, downloadedImage.Value.ContentType);
    }

    var staleCachedThumbnail = await TryReadAnyCachedImageAsync(thumbnailCacheDirectory, cancellationToken);
    if (staleCachedThumbnail is not null)
    {
        return Results.File(staleCachedThumbnail.Value.Bytes, staleCachedThumbnail.Value.ContentType);
    }

    if (string.IsNullOrWhiteSpace(post.Url))
    {
        return Results.NotFound();
    }

    if (!enableThumbnailCaptureFallback)
    {
        return Results.NotFound();
    }

    var screenshotBytes = await TryCaptureThumbnailFromReelAsync(post.Url, thumbnailCaptureGate, cancellationToken);
    if (screenshotBytes.Length == 0)
    {
        return Results.NotFound();
    }

    var capturedThumbnail = new DownloadedImage(screenshotBytes, "image/jpeg");
    await SaveCachedImageAsync(thumbnailCacheDirectory, post.Url, capturedThumbnail, cancellationToken);
    await BroadcastMediaReadyAsync(mediaHubContext, accountId, postId, "thumbnail", cancellationToken);
    return Results.File(capturedThumbnail.Bytes, capturedThumbnail.ContentType);
});

app.MapGet("/media/accounts/{accountId:guid}/avatar", async (
    Guid accountId,
    ITrackedAccountRepository trackedAccountRepository,
    IHttpClientFactory httpClientFactory,
    IHubContext<AdminEventsHub> mediaHubContext,
    CancellationToken cancellationToken) =>
{
    var account = await trackedAccountRepository.GetByIdAsync(accountId, cancellationToken);
    if (account is null || string.IsNullOrWhiteSpace(account.ProfileImageUrl))
    {
        return Results.NotFound();
    }

    var avatarCacheDirectory = Path.Combine(mediaCacheRoot, "accounts", accountId.ToString("N"), "avatar");
    var cachedAvatar = await TryReadCachedImageAsync(avatarCacheDirectory, account.ProfileImageUrl, cancellationToken);
    if (cachedAvatar is not null)
    {
        return Results.File(cachedAvatar.Value.Bytes, cachedAvatar.Value.ContentType);
    }

    var downloadedImage = await TryDownloadImageAsync(account.ProfileImageUrl, httpClientFactory, cancellationToken);
    if (downloadedImage is not null)
    {
        await SaveCachedImageAsync(avatarCacheDirectory, account.ProfileImageUrl, downloadedImage.Value, cancellationToken);
        await BroadcastMediaReadyAsync(mediaHubContext, accountId, null, "avatar", cancellationToken);
        return Results.File(downloadedImage.Value.Bytes, downloadedImage.Value.ContentType);
    }

    var staleCachedAvatar = await TryReadAnyCachedImageAsync(avatarCacheDirectory, cancellationToken);
    if (staleCachedAvatar is not null)
    {
        return Results.File(staleCachedAvatar.Value.Bytes, staleCachedAvatar.Value.ContentType);
    }

    return Results.NotFound();
});

app.MapPost("/api/auth/login", (
    LoginRequest request,
    AdminSessionService sessionService) =>
{
    if (!sessionService.ValidateCredentials(request.Username, request.Password))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(sessionService.CreateSession());
});

app.MapGet("/api/auth/me", (HttpContext httpContext) =>
{
    if (httpContext.Items.TryGetValue(AdminHttpContextItemKeys.Session, out var sessionObject) &&
        sessionObject is ValidatedAdminSession session)
    {
        return Results.Ok(new
        {
            username = session.Username,
            role = session.Role,
            expiresAtUtc = session.ExpiresAtUtc
        });
    }

    return Results.Unauthorized();
});

app.MapPost("/api/integrations/instagram/connect", async (
    ConnectInstagramIntegrationRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await dispatcher.SendAsync(
            new ConnectInstagramIntegrationCommand(request.Username, request.Password),
            cancellationToken);

        return Results.Ok(result);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapPost("/api/integrations/instagram/reconnect", async (
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await dispatcher.SendAsync(new ReconnectInstagramIntegrationCommand(), cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
    catch (TimeoutException exception)
    {
        return Results.Json(
            new { message = exception.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (PlaywrightException exception)
    {
        return Results.Json(
            new { message = exception.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/integrations/instagram/verify-code", async (
    VerifyInstagramCodeRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await dispatcher.SendAsync(new VerifyInstagramIntegrationCodeCommand(request.Code), cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapGet("/api/integrations/instagram", async (
    IQueryDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.QueryAsync(new GetInstagramIntegrationStatusQuery(), cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/api/integrations/instagram/settings", async (
    IInstagramSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    var result = await settingsService.GetViewAsync(cancellationToken);
    return Results.Ok(result);
});

app.MapPut("/api/integrations/instagram/settings", async (
    UpdateInstagramSettingsRequest request,
    IInstagramSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    var result = await settingsService.UpdateAsync(
        new InstagramSettingsUpdate(
            request.Provider,
            request.ApifyBaseUrl,
            request.ApifyActorId,
            request.ApifyApiToken,
            request.ApifyRequestTimeoutSeconds,
            request.ClearApifyApiToken),
        cancellationToken);

    return Results.Ok(result);
});

app.MapPost("/api/accounts", async (
    RegisterTrackedAccountRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.SendAsync(
        new RegisterTrackedAccountCommand(
            request.Handle,
            request.MonitoringPrompt,
            request.MonitoringEnabled,
            Math.Max(1, request.CheckEveryMinutes)),
        cancellationToken);

    return Results.Ok(result);
});

app.MapPut("/api/accounts/{accountId:guid}", async (
    Guid accountId,
    UpdateTrackedAccountMonitoringRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.SendAsync(
        new UpdateTrackedAccountMonitoringCommand(
            accountId,
            request.MonitoringPrompt,
            request.MonitoringEnabled,
            Math.Max(1, request.CheckEveryMinutes)),
        cancellationToken);

    return Results.Ok(result);
});

app.MapPost("/api/accounts/{accountId:guid}/inspect", (
    Guid accountId,
    IInspectionJobQueue inspectionJobQueue) =>
{
    var job = inspectionJobQueue.Enqueue(accountId, "Manual");
    return Results.Accepted($"/api/accounts/{accountId}/inspect/status", job);
});

app.MapGet("/api/accounts/{accountId:guid}/inspect/status", (
    Guid accountId,
    IInspectionJobQueue inspectionJobQueue) =>
{
    var status = inspectionJobQueue.GetLatest(accountId);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

app.MapGet("/api/accounts/{accountId:guid}", async (
    HttpContext httpContext,
    Guid accountId,
    DateTime? fromUtc,
    DateTime? toUtc,
    string? search,
    string? sortBy,
    long? minViews,
    long? minLikes,
    long? minComments,
    long? minShares,
    IQueryDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.QueryAsync(
        new GetTrackedAccountOverviewQuery(
            accountId,
            fromUtc,
            toUtc,
            search ?? string.Empty,
            sortBy ?? "performance",
            minViews,
            minLikes,
            minComments,
            minShares),
        cancellationToken);
    return Results.Ok(ToClientOverviewDto(httpContext, result));
});

app.MapGet("/api/accounts/{accountId:guid}/analysis", async (
    Guid accountId,
    IQueryDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.QueryAsync(new GetTrackedAccountAnalysisQuery(accountId), cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/accounts/{accountId:guid}/backfill", async (
    Guid accountId,
    BackfillTrackedAccountHistoryRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.SendAsync(
        new BackfillTrackedAccountHistoryCommand(accountId, request.BatchSize, request.MaxBatches),
        cancellationToken);

    return Results.Ok(result);
});

app.MapDelete("/api/accounts/{accountId:guid}", async (
    Guid accountId,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var deleted = await dispatcher.SendAsync(new DeleteTrackedAccountCommand(accountId), cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/dashboard/watchlist", async (
    HttpContext httpContext,
    string? search,
    string? sortBy,
    long? minViews,
    long? minLikes,
    long? minComments,
    long? minShares,
    IQueryDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.QueryAsync(
        new GetWatchlistDashboardQuery(
            search ?? string.Empty,
            sortBy ?? "bestMultiplier",
            minViews,
            minLikes,
            minComments,
            minShares),
        cancellationToken);
    return Results.Ok(ToClientWatchlistDashboardDto(httpContext, result));
});

app.MapGet("/api/explorer/search", async (
    string q,
    int? page,
    int? pageSize,
    string? sortBy,
    long? minViews,
    long? minLikes,
    long? minComments,
    long? minShares,
    IQueryDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.QueryAsync(
        new SearchInstagramExplorerQuery(
            q,
            page ?? 1,
            pageSize ?? 50,
            sortBy ?? "views",
            minViews,
            minLikes,
            minComments,
            minShares),
        cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/api/explorer/accounts/{handle}", async (
    string handle,
    IQueryDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.QueryAsync(new GetInstagramExplorerAccountPreviewQuery(handle), cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/explorations", async (
    CreateExplorationRequestRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.SendAsync(
        new CreateExplorationRequestCommand(
            request.AccountHandle,
            request.ResearchPrompt,
            request.SelectedPostExternalIds),
        cancellationToken);

    return Results.Ok(result);
});

app.MapPost("/api/explorations/{requestId:guid}/run", async (
    Guid requestId,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.SendAsync(new RunExplorationRequestCommand(requestId), cancellationToken);
    return Results.Ok(result);
});

static AuraUpBack.Application.Contracts.TrackedAccountOverviewDto ToClientOverviewDto(
    HttpContext httpContext,
    AuraUpBack.Application.Contracts.TrackedAccountOverviewDto overview)
{
    var posts = overview.Posts
        .Select(post => new AuraUpBack.Application.Contracts.PostSummaryDto(
            post.Id,
            post.ExternalId,
            post.Caption,
            post.Url,
            BuildThumbnailProxyUrl(httpContext, overview.Id, post.Id, post.ThumbnailUrl, post.Url),
            post.PublishedAtUtc,
            post.Views,
            post.Likes,
            post.Comments,
            post.Shares,
            post.PerformanceMultiplier,
            post.IsOutlier,
            post.PerformanceLabel,
            post.Transcript,
            post.Topic,
            post.TopicConfidence,
            post.ContentAngle,
            post.HookStyle,
            post.ThemeSummary))
        .ToList();

    return new AuraUpBack.Application.Contracts.TrackedAccountOverviewDto(
        overview.Id,
        overview.Handle,
        overview.DisplayName,
        BuildAccountAvatarProxyUrl(httpContext, overview.Id, overview.ProfileImageUrl),
        overview.Bio,
        overview.FollowersCount,
        overview.MonitoringEnabled,
        overview.MonitoringPrompt,
        overview.CheckEveryMinutes,
        overview.LastResearchSummary,
        overview.LastInspectedAtUtc,
        posts);
}

static AuraUpBack.Application.Contracts.WatchlistDashboardDto ToClientWatchlistDashboardDto(
    HttpContext httpContext,
    AuraUpBack.Application.Contracts.WatchlistDashboardDto dashboard)
{
    var accounts = dashboard.Accounts
        .Select(account => new AuraUpBack.Application.Contracts.WatchlistAccountItemDto(
            account.AccountId,
            account.Handle,
            account.DisplayName,
            BuildAccountAvatarProxyUrl(httpContext, account.AccountId, account.ProfileImageUrl),
            account.MonitoringEnabled,
            account.LastInspectedAtUtc,
            account.BestMultiplier,
            account.TopViews,
            account.TopLikes,
            account.TopComments,
            account.TopShares,
            account.TotalPosts,
            account.OutlierPosts))
        .ToList();

    return new AuraUpBack.Application.Contracts.WatchlistDashboardDto(
        accounts,
        dashboard.LatestAlerts,
        dashboard.TopReels);
}

static string BuildAccountAvatarProxyUrl(HttpContext httpContext, Guid accountId, string profileImageUrl)
{
    if (string.IsNullOrWhiteSpace(profileImageUrl))
    {
        return string.Empty;
    }

    return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/media/accounts/{accountId}/avatar";
}

static string BuildThumbnailProxyUrl(HttpContext httpContext, Guid accountId, Guid postId, string thumbnailUrl, string reelUrl)
{
    if (string.IsNullOrWhiteSpace(thumbnailUrl) && string.IsNullOrWhiteSpace(reelUrl))
    {
        return string.Empty;
    }

    return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/media/accounts/{accountId}/posts/{postId}/thumbnail";
}

static async Task<string> TryResolveThumbnailFromReelAsync(
    string reelUrl,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken)
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

static string ExtractMetaContent(string rawHtml, string propertyName)
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

static async Task<DownloadedImage?> TryDownloadImageAsync(
    string sourceUrl,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken)
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
    catch (HttpRequestException)
    {
        return null;
    }
    catch (IOException)
    {
        return null;
    }
    catch (TaskCanceledException)
    {
        return null;
    }
}

static async Task<DownloadedImage?> TryReadCachedImageAsync(
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
        return bytes.Length == 0
            ? null
            : new DownloadedImage(bytes, metadata.ContentType);
    }
    catch
    {
        return null;
    }
}

static async Task<DownloadedImage?> TryReadAnyCachedImageAsync(
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
        return bytes.Length == 0
            ? null
            : new DownloadedImage(bytes, metadata.ContentType);
    }
    catch
    {
        return null;
    }
}

static async Task SaveCachedImageAsync(
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

static Task BroadcastInspectionStatusAsync(IHubContext<AdminEventsHub> hubContext, InspectionJobStatus status)
{
    return hubContext.Clients.All.SendAsync(
        "inspectionStatusChanged",
        new
        {
            jobId = status.JobId,
            accountId = status.AccountId,
            source = status.Source,
            status = status.Status,
            queuedAtUtc = status.QueuedAtUtc,
            startedAtUtc = status.StartedAtUtc,
            completedAtUtc = status.CompletedAtUtc,
            error = status.Error,
            currentPhase = status.CurrentPhase,
            currentItem = status.CurrentItem,
            processedPosts = status.ProcessedPosts,
            discoveredPosts = status.DiscoveredPosts,
            newPostsFound = status.NewPostsFound,
            recentItems = status.RecentItems
        });
}

static Task BroadcastMediaReadyAsync(
    IHubContext<AdminEventsHub> hubContext,
    Guid accountId,
    Guid? postId,
    string mediaType,
    CancellationToken cancellationToken)
{
    return hubContext.Clients.All.SendAsync(
        "mediaReady",
        new
        {
            accountId,
            postId,
            mediaType
        },
        cancellationToken);
}

static async Task<byte[]> TryCaptureThumbnailFromReelAsync(
    string reelUrl,
    SemaphoreSlim captureGate,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(reelUrl))
    {
        return [];
    }

    var acquired = false;
    try
    {
        acquired = await captureGate.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        if (!acquired)
        {
            return [];
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ChromiumSandbox = false,
            Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu"
            ]
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
            captureGate.Release();
        }
    }
}

app.MapPost("/api/accounts/{accountId:guid}/posts/{postId:guid}/transcribe", async (
    Guid accountId,
    Guid postId,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.SendAsync(new TranscribeTrackedPostCommand(accountId, postId), cancellationToken);
    return Results.Ok(result);
});

app.Run();

public sealed record RegisterTrackedAccountRequest(
    string Handle,
    string MonitoringPrompt,
    bool MonitoringEnabled,
    int CheckEveryMinutes);

public sealed record UpdateTrackedAccountMonitoringRequest(
    string MonitoringPrompt,
    bool MonitoringEnabled,
    int CheckEveryMinutes);

public sealed record ConnectInstagramIntegrationRequest(
    string Username,
    string Password);

public sealed record VerifyInstagramCodeRequest(string Code);

public sealed record UpdateInstagramSettingsRequest(
    string Provider,
    string ApifyBaseUrl,
    string ApifyActorId,
    string? ApifyApiToken,
    int ApifyRequestTimeoutSeconds,
    bool ClearApifyApiToken = false);

public sealed record CreateExplorationRequestRequest(
    string AccountHandle,
    string ResearchPrompt,
    IReadOnlyCollection<string>? SelectedPostExternalIds);

public sealed record BackfillTrackedAccountHistoryRequest(
    int BatchSize = 12,
    int MaxBatches = 5);

public sealed record LoginRequest(
    string Username,
    string Password);

public readonly record struct DownloadedImage(byte[] Bytes, string ContentType);

public sealed record CachedImageMetadata(string SourceUrl, string ContentType);
