using AuraUpBack.Application.Contracts;
using AuraUpBack.Application.Mappers;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Commands.InspectTrackedAccount;

public sealed record InspectTrackedAccountCommand(Guid AccountId, Guid? JobId = null) : Abstractions.ICommand<TrackedAccountOverviewDto>;

internal sealed class InspectTrackedAccountCommandHandler(
    ITrackedAccountRepository trackedAccountRepository,
    IInstagramResearchAutomation instagramResearchAutomation,
    IAlertSignalRepository alertSignalRepository)
    : Abstractions.ICommandHandler<InspectTrackedAccountCommand, TrackedAccountOverviewDto>
{
    public async Task<TrackedAccountOverviewDto> HandleAsync(InspectTrackedAccountCommand command, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked account was not found.");

        const int batchSize = 12;
        var nowUtc = DateTime.UtcNow;
        var excludedExternalIds = new HashSet<string>(
            account.GetReels().Select(x => x.ExternalId),
            StringComparer.OrdinalIgnoreCase);

        while (true)
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
                    MaxDiscoveryPosts = excludedExternalIds.Count + batchSize,
                    JobId = command.JobId
                },
                cancellationToken);

            account.ApplyInspection(payload, nowUtc);
            await trackedAccountRepository.UpsertAsync(account, cancellationToken);

            foreach (var externalId in payload.SeenPostExternalIds)
            {
                excludedExternalIds.Add(externalId);
            }

            var newlyDiscoveredCandidates = payload.SeenPostExternalIds
                .Count(externalId => !previouslyExcludedExternalIds.Contains(externalId));

            if (newlyDiscoveredCandidates < batchSize)
            {
                break;
            }
        }

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
}
