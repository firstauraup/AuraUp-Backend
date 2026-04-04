using AuraUpBack.Application.Contracts;
using AuraUpBack.Application.Mappers;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;

namespace AuraUpBack.Application.Commands.CreateExplorationRequest;

public sealed record CreateExplorationRequestCommand(
    string AccountHandle,
    string ResearchPrompt,
    IReadOnlyCollection<string>? SelectedPostExternalIds) : Abstractions.ICommand<ExplorationRequestDto>;

internal sealed class CreateExplorationRequestCommandHandler(IExplorationRequestRepository explorationRequestRepository)
    : Abstractions.ICommandHandler<CreateExplorationRequestCommand, ExplorationRequestDto>
{
    public async Task<ExplorationRequestDto> HandleAsync(CreateExplorationRequestCommand command, CancellationToken cancellationToken)
    {
        var request = ExplorationRequest.Create(
            command.AccountHandle,
            command.ResearchPrompt,
            command.SelectedPostExternalIds,
            DateTime.UtcNow);

        await explorationRequestRepository.UpsertAsync(request, cancellationToken);
        return request.ToDto();
    }
}
