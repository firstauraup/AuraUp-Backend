using AuraUpBack.Application.Commands.ConnectInstagramIntegration;
using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Commands.StartInstagramManualLogin;

public sealed record StartInstagramManualLoginCommand() : Abstractions.ICommand<InstagramIntegrationDto>;

internal sealed class StartInstagramManualLoginCommandHandler(
    IInstagramConnectionAutomation instagramConnectionAutomation)
    : Abstractions.ICommandHandler<StartInstagramManualLoginCommand, InstagramIntegrationDto>
{
    public async Task<InstagramIntegrationDto> HandleAsync(StartInstagramManualLoginCommand command, CancellationToken cancellationToken)
    {
        var state = await instagramConnectionAutomation.StartManualLoginAsync(cancellationToken);
        return state.ToDto();
    }
}
