using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Commands.BackfillTrackedAccountHistory;

public sealed record BackfillTrackedAccountHistoryCommand(Guid AccountId, int BatchSize, int MaxBatches)
    : Abstractions.ICommand<BackfillTrackedAccountHistoryDto>;

internal sealed class BackfillTrackedAccountHistoryCommandHandler(
    ITrackedAccountRepository trackedAccountRepository,
    IInstagramResearchAutomation instagramResearchAutomation)
    : Abstractions.ICommandHandler<BackfillTrackedAccountHistoryCommand, BackfillTrackedAccountHistoryDto>
{
    public async Task<BackfillTrackedAccountHistoryDto> HandleAsync(BackfillTrackedAccountHistoryCommand command, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked account was not found.");

        var batchSize = command.BatchSize <= 0 ? 12 : command.BatchSize;
        var maxBatches = command.MaxBatches <= 0 ? 5 : command.MaxBatches;
        var totalNewPosts = 0;
        var executedBatches = 0;
        var reachedEndOfHistory = false;
        var excludedExternalIds = new HashSet<string>(
            account.GetReels().Select(x => x.ExternalId),
            StringComparer.OrdinalIgnoreCase);

        for (var batchNumber = 1; batchNumber <= maxBatches; batchNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    MaxDiscoveryPosts = excludedExternalIds.Count + batchSize
                },
                cancellationToken);

            executedBatches++;

            foreach (var externalId in payload.SeenPostExternalIds)
            {
                excludedExternalIds.Add(externalId);
            }

            var newlyDiscoveredCandidates = payload.SeenPostExternalIds
                .Count(externalId => !previouslyExcludedExternalIds.Contains(externalId));

            if (newlyDiscoveredCandidates == 0)
            {
                reachedEndOfHistory = true;
                break;
            }

            account.ApplyInspection(payload, DateTime.UtcNow);
            await trackedAccountRepository.UpsertAsync(account, cancellationToken);
            totalNewPosts += payload.Posts.Count;

            if (newlyDiscoveredCandidates < batchSize)
            {
                reachedEndOfHistory = true;
                break;
            }
        }

        return new BackfillTrackedAccountHistoryDto(
            account.Id,
            account.Handle,
            executedBatches,
            totalNewPosts,
            reachedEndOfHistory,
            DateTime.UtcNow);
    }
}
