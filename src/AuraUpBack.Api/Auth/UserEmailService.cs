using AuraUpBack.Application.Abstractions;
using AuraUpBack.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AuraUpBack.Api.Auth;

public sealed class UserEmailService(
    IEmailNotificationService emailNotificationService,
    ILogger<UserEmailService> logger)
{
    public async Task SendInvitationAsync(
        string email,
        string invitationUrl,
        AppUserRole role,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        await emailNotificationService.SendInvitationAsync(email, role, invitationUrl, expiresAtUtc, cancellationToken);
        logger.LogInformation("Invitation email dispatched to {Email} for role {Role}.", email, role);
    }
}
