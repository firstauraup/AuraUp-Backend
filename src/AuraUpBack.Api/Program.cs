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
using AuraUpBack.Api.Media;
using AuraUpBack.Api.Realtime;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using AuraUpBack.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Playwright;

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
builder.Services.AddSingleton<IThumbnailCacheQueue, InMemoryThumbnailCacheQueue>();
builder.Services.AddSingleton<ThumbnailProxyService>();
builder.Services.AddHostedService<ThumbnailCacheBackgroundService>();
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
    ThumbnailProxyService thumbnailProxyService,
    IThumbnailCacheQueue thumbnailCacheQueue,
    CancellationToken cancellationToken) =>
{
    var account = await trackedAccountRepository.GetByIdAsync(accountId, cancellationToken);
    var post = account?.Posts.FirstOrDefault(x => x.Id == postId);
    if (post is null)
    {
        return Results.NotFound();
    }

    var thumbnailCacheDirectory = thumbnailProxyService.GetThumbnailCacheDirectory(accountId, postId);
    var sourceUrl = post.ThumbnailUrl;
    if (!string.IsNullOrWhiteSpace(sourceUrl))
    {
        var cachedThumbnail = await thumbnailProxyService.TryReadCachedImageAsync(
            thumbnailCacheDirectory,
            sourceUrl,
            cancellationToken);

        if (cachedThumbnail is not null)
        {
            return Results.File(cachedThumbnail.Value.Bytes, cachedThumbnail.Value.ContentType);
        }
    }

    var staleCachedThumbnail = await thumbnailProxyService.TryReadAnyCachedImageAsync(thumbnailCacheDirectory, cancellationToken);
    if (staleCachedThumbnail is not null)
    {
        return Results.File(staleCachedThumbnail.Value.Bytes, staleCachedThumbnail.Value.ContentType);
    }

    if (!string.IsNullOrWhiteSpace(post.Url) || !string.IsNullOrWhiteSpace(sourceUrl))
    {
        thumbnailCacheQueue.Enqueue(accountId, postId);
    }

    return Results.NotFound();
});

app.MapGet("/media/accounts/{accountId:guid}/avatar", async (
    Guid accountId,
    ITrackedAccountRepository trackedAccountRepository,
    ThumbnailProxyService thumbnailProxyService,
    IHubContext<AdminEventsHub> mediaHubContext,
    CancellationToken cancellationToken) =>
{
    var account = await trackedAccountRepository.GetByIdAsync(accountId, cancellationToken);
    if (account is null || string.IsNullOrWhiteSpace(account.ProfileImageUrl))
    {
        return Results.NotFound();
    }

    var avatarCacheDirectory = Path.Combine(mediaCacheRoot, "accounts", accountId.ToString("N"), "avatar");
    var cachedAvatar = await thumbnailProxyService.TryReadCachedImageAsync(avatarCacheDirectory, account.ProfileImageUrl, cancellationToken);
    if (cachedAvatar is not null)
    {
        return Results.File(cachedAvatar.Value.Bytes, cachedAvatar.Value.ContentType);
    }

    var downloadedImage = await thumbnailProxyService.TryDownloadImageAsync(account.ProfileImageUrl, cancellationToken);
    if (downloadedImage is not null)
    {
        await thumbnailProxyService.SaveCachedImageAsync(avatarCacheDirectory, account.ProfileImageUrl, downloadedImage.Value, cancellationToken);
        await BroadcastMediaReadyAsync(mediaHubContext, accountId, null, "avatar", cancellationToken);
        return Results.File(downloadedImage.Value.Bytes, downloadedImage.Value.ContentType);
    }

    var staleCachedAvatar = await thumbnailProxyService.TryReadAnyCachedImageAsync(avatarCacheDirectory, cancellationToken);
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
    string? sortBy,
    long? minViews,
    long? minLikes,
    long? minComments,
    long? minShares,
    IQueryDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await dispatcher.QueryAsync(
        new GetTrackedAccountAnalysisQuery(
            accountId,
            sortBy ?? "performance",
            minViews,
            minLikes,
            minComments,
            minShares),
        cancellationToken);
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
