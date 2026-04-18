using AuraUpBack.Application.Contracts;
using AuraUpBack.Application.Mappers;
using AuraUpBack.Application.Abstractions;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Commands.InspectTrackedAccount;

public sealed record InspectTrackedAccountCommand(Guid AccountId, Guid? JobId = null) : Abstractions.ICommand<TrackedAccountOverviewDto>;

internal sealed class InspectTrackedAccountCommandHandler(
    ITrackedAccountRepository trackedAccountRepository,
    IInstagramResearchAutomation instagramResearchAutomation,
    IAlertSignalRepository alertSignalRepository,
    IInspectionProgressReporter inspectionProgressReporter)
    : Abstractions.ICommandHandler<InspectTrackedAccountCommand, TrackedAccountOverviewDto>
{
    public async Task<TrackedAccountOverviewDto> HandleAsync(InspectTrackedAccountCommand command, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked account was not found.");

        const int batchSize = 12;
        var nowUtc = DateTime.UtcNow;
        var totalProcessedPosts = 0;
        var totalDiscoveredPosts = 0;
        var totalNewPostsFound = 0;
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

            if (newlyDiscoveredCandidates < batchSize)
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
            await alertSignalRepository.AddAsync(
                new AlertSignal
                {
                    AccountId = account.Id,
                    ExternalPostId = strongestPost.ExternalId,
                    Severity = strongestPost.PerformanceMultiplier >= 10m ? "critical" : "high",
                    Title = $"Outlier detectado en @{account.Handle}",
                    Message = $"El post {strongestPost.ExternalId} alcanzó {strongestPost.PerformanceLabel} vs el promedio.",
                    CreatedAtUtc = DateTime.UtcNow
                },
                cancellationToken);
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
