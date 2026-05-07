using AuraUpBack.Application.Contracts;
using AuraUpBack.Application.Mappers;
using AuraUpBack.Application.Abstractions;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Domain.Services;
using Microsoft.Extensions.Logging;

namespace AuraUpBack.Application.Commands.InspectTrackedAccount;

public sealed record InspectTrackedAccountCommand(Guid AccountId, Guid? JobId = null) : Abstractions.ICommand<TrackedAccountOverviewDto>;

internal sealed class InspectTrackedAccountCommandHandler(
    ITrackedAccountRepository trackedAccountRepository,
    IInstagramResearchAutomation instagramResearchAutomation,
    IAlertSignalRepository alertSignalRepository,
    IEmailNotificationService emailNotificationService,
    IInspectionProgressReporter inspectionProgressReporter,
    ILogger<InspectTrackedAccountCommandHandler> logger)
    : Abstractions.ICommandHandler<InspectTrackedAccountCommand, TrackedAccountOverviewDto>
{
    public async Task<TrackedAccountOverviewDto> HandleAsync(InspectTrackedAccountCommand command, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked account was not found.");

        const int batchSize = 30;
        const int existingPostsRefreshCount = 12;
        const int maxBatches = 1;
        const int maxConsecutiveEmptyBatches = 1;
        var nowUtc = DateTime.UtcNow;
        var totalProcessedPosts = 0;
        var totalDiscoveredPosts = 0;
        var totalNewPostsFound = 0;
        var batchesRun = 0;
        var consecutiveEmptyBatches = 0;
        var excludedExternalIds = new HashSet<string>(
            account.GetReels().Select(x => x.ExternalId),
            StringComparer.OrdinalIgnoreCase);
        var refreshExistingPostsCount = Math.Min(existingPostsRefreshCount, excludedExternalIds.Count);
        var maxDiscoveryPosts = batchSize + refreshExistingPostsCount;

        ReportProgress(
            command.JobId,
            "Preparing latest reel sync",
            $"Starting inspection for @{account.Handle}",
            totalProcessedPosts,
            totalDiscoveredPosts,
            totalNewPostsFound);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReportProgress(
                command.JobId,
                "Inspecting batch",
                $"Fetching next reel batch for @{account.Handle}",
                totalProcessedPosts,
                totalDiscoveredPosts,
                totalNewPostsFound);

            var previouslyExcludedExternalIds = excludedExternalIds
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var payload = await instagramResearchAutomation.InspectAccountAsync(
                new InstagramInspectionRequest
                {
                    Handle = account.Handle,
                    ResearchPrompt = account.MonitoringPrompt,
                    KnownPostExternalIds = excludedExternalIds.ToArray(),
                    StartFromPostIndex = 0,
                    DesiredNewPosts = batchSize,
                    MaxDiscoveryPosts = maxDiscoveryPosts,
                    RefreshExistingPostsCount = refreshExistingPostsCount,
                    JobId = command.JobId
                },
                cancellationToken);

            account.ApplyInspection(payload, nowUtc);
            await trackedAccountRepository.UpsertAsync(account, cancellationToken);

            totalProcessedPosts += payload.Posts.Count;
            totalDiscoveredPosts += payload.SeenPostExternalIds.Count;

            foreach (var externalId in payload.SeenPostExternalIds)
            {
                excludedExternalIds.Add(externalId);
            }

            var newlyDiscoveredCandidates = payload.SeenPostExternalIds
                .Count(externalId => !previouslyExcludedExternalIds.Contains(externalId));
            var newlyInspectedPosts = payload.Posts
                .Count(post => !previouslyExcludedExternalIds.Contains(post.ExternalId));
            totalNewPostsFound += newlyInspectedPosts;

            ReportProgress(
                command.JobId,
                "Batch processed",
                $"Processed {newlyInspectedPosts} new reels and refreshed {payload.Posts.Count - newlyInspectedPosts} existing reels for @{account.Handle}",
                totalProcessedPosts,
                totalDiscoveredPosts,
                totalNewPostsFound);

            batchesRun++;

            if (payload.Posts.Count == 0 && newlyDiscoveredCandidates == 0)
            {
                consecutiveEmptyBatches++;
            }
            else
            {
                consecutiveEmptyBatches = 0;
            }

            if (consecutiveEmptyBatches >= maxConsecutiveEmptyBatches)
            {
                break;
            }

            if (batchesRun >= maxBatches)
            {
                break;
            }
        }

        ReportProgress(
            command.JobId,
            "Finalizing analysis",
            $"Completing inspection for @{account.Handle}",
            totalProcessedPosts,
            totalDiscoveredPosts,
            totalNewPostsFound);

        var notificationThreshold = Math.Max(2, account.OutlierNotificationMultiplier);
        var postsToNotify = account.GetReels()
            .Where(x => x.PerformanceMultiplier >= notificationThreshold)
            .OrderByDescending(x => x.PerformanceMultiplier)
            .ThenByDescending(x => x.Views)
            .ToList();

        foreach (var post in postsToNotify)
        {
            var notificationMultiplier = ResolveNotificationMultiplier(post.PerformanceMultiplier);
            var highestNotifiedMultiplier = await alertSignalRepository.GetHighestNotificationMultiplierAsync(
                account.Id,
                post.ExternalId,
                cancellationToken);

            if (highestNotifiedMultiplier >= notificationMultiplier)
            {
                continue;
            }

            var alert = new AlertSignal
            {
                AccountId = account.Id,
                ExternalPostId = post.ExternalId,
                Severity = notificationMultiplier >= 10 ? "critical" : "high",
                Title = $"Outlier detectado en @{account.Handle}",
                Message = $"El reel {post.ExternalId} alcanzó {post.PerformanceLabel} vs el promedio. Umbral configurado: x{notificationThreshold}.",
                NotificationMultiplier = notificationMultiplier,
                CreatedAtUtc = DateTime.UtcNow
            };

            await alertSignalRepository.AddAsync(alert, cancellationToken);
            try
            {
                await emailNotificationService.SendViralAlertAsync(account, post, alert, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Viral alert email failed for account {AccountId} post {PostId}.", account.Id, post.Id);
            }
        }

        return account.ToOverviewDto();
    }

    private static int ResolveNotificationMultiplier(decimal performanceMultiplier)
    {
        return Math.Max(0, (int)Math.Floor(performanceMultiplier));
    }

    private void ReportProgress(
        Guid? jobId,
        string phase,
        string currentItem,
        int processedPosts,
        int discoveredPosts,
        int newPostsFound)
    {
        if (!jobId.HasValue)
        {
            return;
        }

        inspectionProgressReporter.Report(
            jobId.Value,
            phase,
            currentItem,
            processedPosts,
            discoveredPosts,
            newPostsFound);
    }
}
