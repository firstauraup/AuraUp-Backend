using AuraUpBack.Application.Commands.ConnectInstagramIntegration;
using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Commands.CompleteInstagramManualLogin;

public sealed record CompleteInstagramManualLoginCommand() : Abstractions.ICommand<InstagramIntegrationDto>;

internal sealed class CompleteInstagramManualLoginCommandHandler(
    IInstagramConnectionAutomation instagramConnectionAutomation)
    : Abstractions.ICommandHandler<CompleteInstagramManualLoginCommand, InstagramIntegrationDto>
{
    public async Task<InstagramIntegrationDto> HandleAsync(CompleteInstagramManualLoginCommand command, CancellationToken cancellationToken)
    {
        var state = await instagramConnectionAutomation.CompleteManualLoginAsync(cancellationToken);
        return state.ToDto();
    }
}
