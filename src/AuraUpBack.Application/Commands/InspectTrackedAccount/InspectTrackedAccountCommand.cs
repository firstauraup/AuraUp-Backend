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
        const int maxBatches = 100;
        const int maxConsecutiveEmptyBatches = 3;
        var nowUtc = DateTime.UtcNow;
        var totalProcessedPosts = 0;
        var totalDiscoveredPosts = 0;
        var totalNewPostsFound = 0;
        var batchesRun = 0;
        var consecutiveEmptyBatches = 0;
        var excludedExternalIds = new HashSet<string>(
            account.GetReels().Select(x => x.ExternalId),
            StringComparer.OrdinalIgnoreCase);

        ReportProgress(
            command.JobId,
            "Preparing full reel sync",
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
                    StartFromPostIndex = excludedExternalIds.Count,
                    DesiredNewPosts = batchSize,
                    MaxDiscoveryPosts = excludedExternalIds.Count + (batchSize * 4),
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
            totalNewPostsFound += payload.Posts.Count;

            ReportProgress(
                command.JobId,
                "Batch processed",
                $"Processed {payload.Posts.Count} new reels for @{account.Handle}",
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

        var strongestPost = account.GetReels()
            .Where(x => x.IsOutlier)
            .OrderByDescending(x => x.PerformanceMultiplier)
            .FirstOrDefault();

        if (strongestPost is not null)
        {
            var alertAlreadyExists = await alertSignalRepository.ExistsAsync(account.Id, strongestPost.ExternalId, cancellationToken);
            if (!alertAlreadyExists)
            {
                var alert = new AlertSignal
                {
                    AccountId = account.Id,
                    ExternalPostId = strongestPost.ExternalId,
                    Severity = strongestPost.PerformanceMultiplier >= 10m ? "critical" : "high",
                    Title = $"Outlier detectado en @{account.Handle}",
                    Message = $"El post {strongestPost.ExternalId} alcanzó {strongestPost.PerformanceLabel} vs el promedio.",
                    CreatedAtUtc = DateTime.UtcNow
                };

                await alertSignalRepository.AddAsync(alert, cancellationToken);
                try
                {
                    await emailNotificationService.SendViralAlertAsync(account, strongestPost, alert, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Viral alert email failed for account {AccountId} post {PostId}.", account.Id, strongestPost.Id);
                }
            }
        }

        return account.ToOverviewDto();
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
