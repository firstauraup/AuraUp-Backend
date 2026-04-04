using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Commands.ConnectInstagramIntegration;

public sealed record ConnectInstagramIntegrationCommand(string Username, string Password) : Abstractions.ICommand<InstagramIntegrationDto>;

internal sealed class ConnectInstagramIntegrationCommandHandler(
    IInstagramConnectionAutomation instagramConnectionAutomation)
    : Abstractions.ICommandHandler<ConnectInstagramIntegrationCommand, InstagramIntegrationDto>
{
    public async Task<InstagramIntegrationDto> HandleAsync(ConnectInstagramIntegrationCommand command, CancellationToken cancellationToken)
    {
        var state = await instagramConnectionAutomation.ConnectAsync(command.Username, command.Password, cancellationToken);
        return state.ToDto();
    }
}

internal static class InstagramIntegrationMappings
{
    public static InstagramIntegrationDto ToDto(this InstagramConnectionState state)
    {
        return new InstagramIntegrationDto(
            state.Provider,
            state.Username,
            state.Status.ToString(),
            state.HasStoredCredentials,
            state.SessionStatePath,
            state.SessionStateExists,
            state.VerificationUrl,
            state.LastLoginAtUtc,
            state.LastValidatedAtUtc,
            state.LastError,
            state.Headless,
            state.MaxPosts,
            state.AllowPublicProfileReadWithoutSession);
    }
}
