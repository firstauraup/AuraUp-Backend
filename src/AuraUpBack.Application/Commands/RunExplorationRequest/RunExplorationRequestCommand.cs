using AuraUpBack.Application.Contracts;
using AuraUpBack.Application.Mappers;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Commands.RunExplorationRequest;

public sealed record RunExplorationRequestCommand(Guid RequestId) : Abstractions.ICommand<ExplorationRequestDto>;

internal sealed class RunExplorationRequestCommandHandler(
    IExplorationRequestRepository explorationRequestRepository,
    ITrackedAccountRepository trackedAccountRepository,
    IInstagramResearchAutomation instagramResearchAutomation)
    : Abstractions.ICommandHandler<RunExplorationRequestCommand, ExplorationRequestDto>
{
    public async Task<ExplorationRequestDto> HandleAsync(RunExplorationRequestCommand command, CancellationToken cancellationToken)
    {
        var request = await explorationRequestRepository.GetByIdAsync(command.RequestId, cancellationToken)
            ?? throw new InvalidOperationException("Exploration request was not found.");

        try
        {
            var nowUtc = DateTime.UtcNow;
            var account = await trackedAccountRepository.GetByHandleAsync(request.AccountHandle, cancellationToken)
                ?? TrackedAccount.Create(request.AccountHandle, request.ResearchPrompt, false, 60, 2, nowUtc);

            var knownReels = account.GetReels();

            var inspection = await instagramResearchAutomation.InspectAccountAsync(
                new Domain.Models.InstagramInspectionRequest
                {
                    Handle = request.AccountHandle,
                    ResearchPrompt = request.ResearchPrompt,
                    KnownPostExternalIds = knownReels.Select(x => x.ExternalId).ToArray(),
                    StartFromPostIndex = 0,
                    MaxDiscoveryPosts = knownReels.Count + 12,
                    DesiredNewPosts = 12,
                    RefreshExistingPostsCount = 12
                },
                cancellationToken);

            account.ApplyInspection(inspection, nowUtc);
            await trackedAccountRepository.UpsertAsync(account, cancellationToken);
            request.MarkCompleted(inspection.ResearchSummary, nowUtc);
        }
        catch (Exception exception)
        {
            request.MarkFailed(exception.Message, DateTime.UtcNow);
        }

        await explorationRequestRepository.UpsertAsync(request, cancellationToken);
        return request.ToDto();
    }
}
