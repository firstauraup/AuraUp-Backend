using AuraUpBack.Application.Commands.ConnectInstagramIntegration;
using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Commands.VerifyInstagramIntegrationCode;

public sealed record VerifyInstagramIntegrationCodeCommand(string Code) : Abstractions.ICommand<InstagramIntegrationDto>;

internal sealed class VerifyInstagramIntegrationCodeCommandHandler(
    IInstagramConnectionAutomation instagramConnectionAutomation)
    : Abstractions.ICommandHandler<VerifyInstagramIntegrationCodeCommand, InstagramIntegrationDto>
{
    public async Task<InstagramIntegrationDto> HandleAsync(VerifyInstagramIntegrationCodeCommand command, CancellationToken cancellationToken)
    {
        var state = await instagramConnectionAutomation.VerifyCodeAsync(command.Code, cancellationToken);
        return state.ToDto();
    }
}
