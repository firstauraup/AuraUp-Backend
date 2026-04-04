using AuraUpBack.Application.Commands.ConnectInstagramIntegration;
using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Commands.ReconnectInstagramIntegration;

public sealed record ReconnectInstagramIntegrationCommand() : Abstractions.ICommand<InstagramIntegrationDto>;

internal sealed class ReconnectInstagramIntegrationCommandHandler(
    IInstagramConnectionAutomation instagramConnectionAutomation)
    : Abstractions.ICommandHandler<ReconnectInstagramIntegrationCommand, InstagramIntegrationDto>
{
    public async Task<InstagramIntegrationDto> HandleAsync(ReconnectInstagramIntegrationCommand command, CancellationToken cancellationToken)
    {
        var state = await instagramConnectionAutomation.ReconnectAsync(cancellationToken);
        return state.ToDto();
    }
}
