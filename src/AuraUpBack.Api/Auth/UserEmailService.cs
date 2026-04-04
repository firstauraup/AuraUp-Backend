using Microsoft.Extensions.Logging;

namespace AuraUpBack.Api.Auth;

public sealed class UserEmailService(ILogger<UserEmailService> logger)
{
    public Task SendInvitationAsync(string email, string invitationUrl, string roleName, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "User invitation prepared for {Email} with role {Role}. Registration URL: {InvitationUrl}",
            email,
            roleName,
            invitationUrl);

        return Task.CompletedTask;
    }
}
