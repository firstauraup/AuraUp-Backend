using AuraUpBack.Application;
using AuraUpBack.Application.Abstractions;
using AuraUpBack.Application.Commands.CreateExplorationRequest;
using AuraUpBack.Application.Commands.ConnectInstagramIntegration;
using AuraUpBack.Application.Commands.CompleteInstagramManualLogin;
using AuraUpBack.Application.Commands.BackfillTrackedAccountHistory;
using AuraUpBack.Application.Commands.DeleteTrackedAccount;
using AuraUpBack.Application.Commands.InspectTrackedAccount;
using AuraUpBack.Application.Commands.RegisterTrackedAccount;
using AuraUpBack.Application.Commands.ReconnectInstagramIntegration;
using AuraUpBack.Application.Commands.RunExplorationRequest;
using AuraUpBack.Application.Commands.StartInstagramManualLogin;
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
using AuraUpBack.Infrastructure;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using AuraUpBack.Infrastructure.Services;
using AuraUpBack.Domain.Enums;
using AuraUpBack.Domain.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Playwright;

var builder = WebApplication.CreateBuilder(args);
var allowedCorsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?.Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Select(origin => origin.Trim().TrimEnd('/'))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray()
    ?? [
        "https://www.auraup.org", 
        "https://auraup.org",
        "http://localhost:5173",
        "https://localhost:5173",
        "http://localhost:5000",
        "https://localhost:5000"];

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, enableMonitoringService: true);
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection(AdminAuthOptions.SectionName));
builder.Services.AddSingleton<AdminSessionService>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<UserInvitationService>();
builder.Services.AddSingleton<UserEmailService>();
builder.Services.AddSingleton<IThumbnailCacheQueue, InMemoryThumbnailCacheQueue>();
builder.Services.AddHostedService<ThumbnailCacheBackgroundService>();
builder.Services.AddSignalR();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
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

var app = builder.Build();
var inspectionJobQueue = app.Services.GetRequiredService<IInspectionJobQueue>();
var hubContext = app.Services.GetRequiredService<IHubContext<AdminEventsHub>>();

inspectionJobQueue.StatusChanged += status =>
{
    _ = BroadcastInspectionStatusAsync(hubContext, status);
};

app.UseForwardedHeaders();
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
    IMediaAssetStorage mediaAssetStorage,
    IThumbnailCacheQueue thumbnailCacheQueue,
    CancellationToken cancellationToken) =>
{
    var account = await trackedAccountRepository.GetByIdAsync(accountId, cancellationToken);
    var post = account?.Posts.FirstOrDefault(x => x.Id == postId);
    if (post is null)
    {
        return Results.NotFound();
    }

    var signedUrl = await mediaAssetStorage.GetSignedPostThumbnailUrlAsync(
        accountId,
        postId,
        post.ThumbnailUrl,
        post.ThumbnailObjectKey,
        post.Url,
        cancellationToken);

    if (!string.IsNullOrWhiteSpace(signedUrl))
    {
        return Results.Redirect(signedUrl);
    }

    if (!string.IsNullOrWhiteSpace(post.Url) || !string.IsNullOrWhiteSpace(post.ThumbnailUrl))
    {
        thumbnailCacheQueue.Enqueue(accountId, postId);
    }

    return Results.NotFound();
});

app.MapGet("/media/accounts/{accountId:guid}/avatar", async (
    Guid accountId,
    ITrackedAccountRepository trackedAccountRepository,
    IMediaAssetStorage mediaAssetStorage,
    IHubContext<AdminEventsHub> mediaHubContext,
    CancellationToken cancellationToken) =>
{
    var account = await trackedAccountRepository.GetByIdAsync(accountId, cancellationToken);
    if (account is null || string.IsNullOrWhiteSpace(account.ProfileImageUrl))
    {
        return Results.NotFound();
    }

    var signedUrl = await mediaAssetStorage.GetSignedAvatarUrlAsync(
        accountId,
        account.ProfileImageUrl,
        account.ProfileImageObjectKey,
        cancellationToken);

    if (!string.IsNullOrWhiteSpace(signedUrl))
    {
        await BroadcastMediaReadyAsync(mediaHubContext, accountId, null, "avatar", cancellationToken);
        return Results.Redirect(signedUrl);
    }

    return Results.NotFound();
});

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    AdminSessionService sessionService,
    CancellationToken cancellationToken) =>
{
    var session = await sessionService.ValidateCredentialsAsync(request.Username, request.Password, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(session);
});

app.MapGet("/api/auth/me", async (
    HttpContext httpContext,
    IAppUserRepository userRepository,
    CancellationToken cancellationToken) =>
{
    if (httpContext.Items.TryGetValue(AdminHttpContextItemKeys.Session, out var sessionObject) &&
        sessionObject is ValidatedAdminSession session)
    {
        var user = await userRepository.GetByIdAsync(session.UserId, cancellationToken);
        return Results.Ok(new
        {
            userId = session.UserId,
            email = session.Email,
            role = session.Role,
            expiresAtUtc = session.ExpiresAtUtc,
            firstName = user?.FirstName ?? string.Empty,
            lastName = user?.LastName ?? string.Empty,
            assignedAccountIds = user?.AssignedAccounts.Select(x => x.AccountId).ToArray() ?? []
        });
    }

    return Results.Unauthorized();
});

app.MapGet("/api/admin/users", async (
    HttpContext httpContext,
    IAppUserRepository userRepository,
    CancellationToken cancellationToken) =>
{
    var session = httpContext.RequireSession();
    if (!session.IsAdministrator())
    {
        return AuthorizationExtensions.ForbidAction("Only administrators can list users.");
    }

    var users = await userRepository.GetAllAsync(cancellationToken);
    return Results.Ok(users.Select(user => new
    {
        user.Id,
        user.Email,
        role = user.Role.ToString().ToLowerInvariant(),
        status = user.Status.ToString().ToLowerInvariant(),
        user.FirstName,
        user.LastName,
        assignedAccountIds = user.AssignedAccounts.Select(x => x.AccountId).ToArray(),
        user.CreatedAtUtc,
        user.ActivatedAtUtc,
        user.LastLoginAtUtc
    }));
});

app.MapPost("/api/admin/users/invitations", async (
    HttpContext httpContext,
    InviteUserRequest request,
    UserInvitationService invitationService,
    UserEmailService userEmailService,
    CancellationToken cancellationToken) =>
{
    var session = httpContext.RequireSession();
    if (!session.IsAdministrator())
    {
        return AuthorizationExtensions.ForbidAction("Only administrators can invite users.");
    }

    if (!Enum.TryParse<AppUserRole>(request.Role, true, out var role))
    {
        return Results.BadRequest(new { message = "Role is invalid." });
    }

    var accountIds = role == AppUserRole.Client
        ? request.AssignedAccountIds?.Distinct().ToArray() ?? []
        : [];
    var invitation = await invitationService.InviteAsync(request.Email, role, accountIds, cancellationToken);
    await userEmailService.SendInvitationAsync(invitation.User.Email, invitation.InvitationUrl, role.ToString(), cancellationToken);

    return Results.Ok(new
    {
        userId = invitation.User.Id,
        email = invitation.User.Email,
        role = role.ToString().ToLowerInvariant(),
        invitationUrl = invitation.InvitationUrl,
        expiresAtUtc = invitation.Invitation.ExpiresAtUtc
    });
});

app.MapGet("/api/onboarding/invitations/{token}", async (
    string token,
    UserInvitationService invitationService,
    CancellationToken cancellationToken) =>
{
    var invitation = await invitationService.GetInvitationAsync(token, cancellationToken);
    return invitation is null ? Results.NotFound() : Results.Ok(invitation);
});

app.MapPost("/api/onboarding/register", async (
    CompleteInvitationRequest request,
    UserInvitationService invitationService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var user = await invitationService.CompleteRegistrationAsync(
            request.Token,
            request.FirstName,
            request.LastName,
            request.PhoneNumber,
            request.City,
            request.Country,
            request.CompanyName,
            request.Password,
            cancellationToken);

        return Results.Ok(new
        {
            user.Id,
            user.Email,
            role = user.Role.ToString().ToLowerInvariant(),
            status = user.Status.ToString().ToLowerInvariant()
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapPost("/api/integrations/instagram/connect", async (
    HttpContext httpContext,
    ConnectInstagramIntegrationRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.RequireSession().IsAdministrator())
    {
        return AuthorizationExtensions.ForbidAction("Only administrators can manage Instagram integration.");
    }

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
    HttpContext httpContext,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.RequireSession().IsAdministrator())
    {
        return AuthorizationExtensions.ForbidAction("Only administrators can manage Instagram integration.");
    }

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

app.MapPost("/api/integrations/instagram/manual/start", async (
    HttpContext httpContext,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.RequireSession().IsAdministrator())
    {
        return AuthorizationExtensions.ForbidAction("Only administrators can manage Instagram integration.");
    }

    try
    {
        var result = await dispatcher.SendAsync(new StartInstagramManualLoginCommand(), cancellationToken);
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

app.MapPost("/api/integrations/instagram/manual/complete", async (
    HttpContext httpContext,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.RequireSession().IsAdministrator())
    {
        return AuthorizationExtensions.ForbidAction("Only administrators can manage Instagram integration.");
    }

    try
    {
        var result = await dispatcher.SendAsync(new CompleteInstagramManualLoginCommand(), cancellationToken);
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
    HttpContext httpContext,
    VerifyInstagramCodeRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.RequireSession().IsAdministrator())
    {
        return AuthorizationExtensions.ForbidAction("Only administrators can manage Instagram integration.");
    }

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
    HttpContext httpContext,
    IQueryDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.RequireSession().IsAdministrator())
    {
        return AuthorizationExtensions.ForbidAction("Only administrators can view Instagram integration.");
    }

    var result = await dispatcher.QueryAsync(new GetInstagramIntegrationStatusQuery(), cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/api/integrations/instagram/settings", async (
    HttpContext httpContext,
    IInstagramSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.RequireSession().IsAdministrator())
    {
        return AuthorizationExtensions.ForbidAction("Only administrators can view Instagram settings.");
    }

    var result = await settingsService.GetViewAsync(cancellationToken);
    return Results.Ok(result);
});

app.MapPut("/api/integrations/instagram/settings", async (
    HttpContext httpContext,
    UpdateInstagramSettingsRequest request,
    IInstagramSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.RequireSession().IsAdministrator())
    {
        return AuthorizationExtensions.ForbidAction("Only administrators can change Instagram settings.");
    }

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
    HttpContext httpContext,
    RegisterTrackedAccountRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var session = httpContext.RequireSession();
    if (session.IsClient())
    {
        return AuthorizationExtensions.ForbidAction("Clients cannot create tracked accounts.");
    }

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
    HttpContext httpContext,
    Guid accountId,
    UpdateTrackedAccountMonitoringRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var session = httpContext.RequireSession();
    if (session.IsClient())
    {
        return AuthorizationExtensions.ForbidAction("Clients cannot edit tracked accounts.");
    }

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
    HttpContext httpContext,
    Guid accountId,
    IInspectionJobQueue inspectionJobQueue,
    InspectionJobRunner inspectionJobRunner) =>
{
    var session = httpContext.RequireSession();
    if (session.IsClient())
    {
        return AuthorizationExtensions.ForbidAction("Clients cannot inspect accounts.");
    }

    var job = inspectionJobQueue.Enqueue(accountId, "Manual");
    inspectionJobRunner.Schedule(job.JobId);
    return Results.Accepted($"/api/accounts/{accountId}/inspect/status", job);
});

app.MapGet("/api/accounts/{accountId:guid}/inspect/status", async (
    HttpContext httpContext,
    Guid accountId,
    IInspectionJobQueue inspectionJobQueue,
    IAppUserRepository userRepository,
    CancellationToken cancellationToken) =>
{
    var session = httpContext.RequireSession();
    if (!await session.CanAccessAccountAsync(accountId, userRepository, cancellationToken))
    {
        return AuthorizationExtensions.ForbidAction("You do not have access to this account.");
    }

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
    IAppUserRepository userRepository,
    IQueryDispatcher dispatcher,
    IMediaAssetStorage mediaAssetStorage,
    CancellationToken cancellationToken) =>
{
    var session = httpContext.RequireSession();
    if (!await session.CanAccessAccountAsync(accountId, userRepository, cancellationToken))
    {
        return AuthorizationExtensions.ForbidAction("You do not have access to this account.");
    }

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
    var clientResult = await ToClientOverviewDtoAsync(result, mediaAssetStorage, cancellationToken);
    return Results.Ok(clientResult);
});

app.MapGet("/api/accounts/{accountId:guid}/analysis", async (
    HttpContext httpContext,
    Guid accountId,
    string? sortBy,
    long? minViews,
    long? minLikes,
    long? minComments,
    long? minShares,
    IAppUserRepository userRepository,
    IQueryDispatcher dispatcher,
    IMediaAssetStorage mediaAssetStorage,
    CancellationToken cancellationToken) =>
{
    var session = httpContext.RequireSession();
    if (!await session.CanAccessAccountAsync(accountId, userRepository, cancellationToken))
    {
        return AuthorizationExtensions.ForbidAction("You do not have access to this account.");
    }

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
    HttpContext httpContext,
    Guid accountId,
    BackfillTrackedAccountHistoryRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (httpContext.RequireSession().IsClient())
    {
        return AuthorizationExtensions.ForbidAction("Clients cannot backfill accounts.");
    }

    var result = await dispatcher.SendAsync(
        new BackfillTrackedAccountHistoryCommand(accountId, request.BatchSize, request.MaxBatches),
        cancellationToken);

    return Results.Ok(result);
});

app.MapDelete("/api/accounts/{accountId:guid}", async (
    HttpContext httpContext,
    Guid accountId,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (httpContext.RequireSession().IsClient())
    {
        return AuthorizationExtensions.ForbidAction("Clients cannot delete accounts.");
    }

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
    IAppUserRepository userRepository,
    IQueryDispatcher dispatcher,
    IMediaAssetStorage mediaAssetStorage,
    CancellationToken cancellationToken) =>
{
    var session = httpContext.RequireSession();
    var result = await dispatcher.QueryAsync(
        new GetWatchlistDashboardQuery(
            search ?? string.Empty,
            sortBy ?? "bestMultiplier",
            minViews,
            minLikes,
            minComments,
            minShares),
        cancellationToken);

    if (session.IsClient())
    {
        var user = await userRepository.GetByIdAsync(session.UserId, cancellationToken);
        var allowedAccountIds = user?.AssignedAccounts.Select(x => x.AccountId).ToHashSet() ?? [];
        var filtered = result with
        {
            Accounts = result.Accounts.Where(x => allowedAccountIds.Contains(x.AccountId)).ToList()
        };

        var clientResult = await ToClientWatchlistDashboardDtoAsync(filtered, mediaAssetStorage, cancellationToken);
        return Results.Ok(clientResult);
    }

    var dashboardResult = await ToClientWatchlistDashboardDtoAsync(result, mediaAssetStorage, cancellationToken);
    return Results.Ok(dashboardResult);
});

app.MapGet("/api/explorer/search", async (
    HttpContext httpContext,
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
    if (httpContext.RequireSession().IsClient())
    {
        return AuthorizationExtensions.ForbidAction("Clients cannot use explorer.");
    }

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
    HttpContext httpContext,
    string handle,
    IQueryDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (httpContext.RequireSession().IsClient())
    {
        return AuthorizationExtensions.ForbidAction("Clients cannot use explorer.");
    }

    var result = await dispatcher.QueryAsync(new GetInstagramExplorerAccountPreviewQuery(handle), cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/explorations", async (
    HttpContext httpContext,
    CreateExplorationRequestRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (httpContext.RequireSession().IsClient())
    {
        return AuthorizationExtensions.ForbidAction("Clients cannot create research requests.");
    }

    var result = await dispatcher.SendAsync(
        new CreateExplorationRequestCommand(
            request.AccountHandle,
            request.ResearchPrompt,
            request.SelectedPostExternalIds),
        cancellationToken);

    return Results.Ok(result);
});

app.MapPost("/api/explorations/{requestId:guid}/run", async (
    HttpContext httpContext,
    Guid requestId,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    if (httpContext.RequireSession().IsClient())
    {
        return AuthorizationExtensions.ForbidAction("Clients cannot run research requests.");
    }

    var result = await dispatcher.SendAsync(new RunExplorationRequestCommand(requestId), cancellationToken);
    return Results.Ok(result);
});

static async Task<AuraUpBack.Application.Contracts.TrackedAccountOverviewDto> ToClientOverviewDtoAsync(
    AuraUpBack.Application.Contracts.TrackedAccountOverviewDto overview,
    IMediaAssetStorage mediaAssetStorage,
    CancellationToken cancellationToken)
{
    var avatarUrl = await mediaAssetStorage.GetSignedAvatarUrlAsync(
        overview.Id,
        overview.ProfileImageUrl,
        overview.ProfileImageObjectKey,
        cancellationToken);

    var posts = new List<AuraUpBack.Application.Contracts.PostSummaryDto>(overview.Posts.Count);
    foreach (var post in overview.Posts)
    {
        var thumbnailUrl = await mediaAssetStorage.GetSignedPostThumbnailUrlAsync(
            overview.Id,
            post.Id,
            post.ThumbnailUrl,
            post.ThumbnailObjectKey,
            post.Url,
            cancellationToken);

        posts.Add(new AuraUpBack.Application.Contracts.PostSummaryDto(
            post.Id,
            post.ExternalId,
            post.Caption,
            post.Url,
            thumbnailUrl,
            post.ThumbnailObjectKey,
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
            post.ThemeSummary));
    }

    return new AuraUpBack.Application.Contracts.TrackedAccountOverviewDto(
        overview.Id,
        overview.Handle,
        overview.DisplayName,
        avatarUrl,
        overview.ProfileImageObjectKey,
        overview.Bio,
        overview.FollowersCount,
        overview.MonitoringEnabled,
        overview.MonitoringPrompt,
        overview.CheckEveryMinutes,
        overview.LastResearchSummary,
        overview.LastInspectedAtUtc,
        posts);
}

static async Task<AuraUpBack.Application.Contracts.WatchlistDashboardDto> ToClientWatchlistDashboardDtoAsync(
    AuraUpBack.Application.Contracts.WatchlistDashboardDto dashboard,
    IMediaAssetStorage mediaAssetStorage,
    CancellationToken cancellationToken)
{
    var accounts = new List<AuraUpBack.Application.Contracts.WatchlistAccountItemDto>(dashboard.Accounts.Count);
    foreach (var account in dashboard.Accounts)
    {
        var avatarUrl = await mediaAssetStorage.GetSignedAvatarUrlAsync(
            account.AccountId,
            account.ProfileImageUrl,
            account.ProfileImageObjectKey,
            cancellationToken);

        accounts.Add(new AuraUpBack.Application.Contracts.WatchlistAccountItemDto(
            account.AccountId,
            account.Handle,
            account.DisplayName,
            avatarUrl,
            account.ProfileImageObjectKey,
            account.MonitoringEnabled,
            account.LastInspectedAtUtc,
            account.BestMultiplier,
            account.TopViews,
            account.TopLikes,
            account.TopComments,
            account.TopShares,
            account.TotalPosts,
            account.OutlierPosts));
    }

    var topReels = new List<AuraUpBack.Application.Contracts.GlobalViralReelDto>(dashboard.TopReels.Count);
    foreach (var reel in dashboard.TopReels)
    {
        var thumbnailUrl = await mediaAssetStorage.GetSignedPostThumbnailUrlAsync(
            reel.AccountId,
            reel.PostId,
            reel.ThumbnailUrl,
            reel.ThumbnailObjectKey,
            reel.Url,
            cancellationToken);

        topReels.Add(new AuraUpBack.Application.Contracts.GlobalViralReelDto(
            reel.AccountId,
            reel.PostId,
            reel.AccountHandle,
            reel.AccountDisplayName,
            reel.ExternalId,
            reel.Caption,
            reel.Url,
            thumbnailUrl,
            reel.ThumbnailObjectKey,
            reel.Views,
            reel.Likes,
            reel.Comments,
            reel.Shares,
            reel.PerformanceMultiplier,
            reel.Topic,
            reel.HookStyle));
    }

    return new AuraUpBack.Application.Contracts.WatchlistDashboardDto(
        accounts,
        dashboard.LatestAlerts,
        topReels);
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
    HttpContext httpContext,
    Guid accountId,
    Guid postId,
    IAppUserRepository userRepository,
    ICommandDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var session = httpContext.RequireSession();
    if (!await session.CanAccessAccountAsync(accountId, userRepository, cancellationToken) || session.IsClient())
    {
        return AuthorizationExtensions.ForbidAction("You do not have permission to transcribe this post.");
    }

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

public sealed record InviteUserRequest(
    string Email,
    string Role,
    IReadOnlyCollection<Guid>? AssignedAccountIds);

public sealed record CompleteInvitationRequest(
    string Token,
    string FirstName,
    string LastName,
    string PhoneNumber,
    string City,
    string Country,
    string CompanyName,
    string Password);
