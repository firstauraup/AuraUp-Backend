using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Enums;

namespace AuraUpBack.Application.Abstractions;

public interface IEmailNotificationService
{
    Task SendApplicationFormSubmittedAsync(ApplicationFormSubmission submission, CancellationToken cancellationToken);
    Task SendInvitationAsync(string email, AppUserRole role, string invitationUrl, DateTime expiresAtUtc, CancellationToken cancellationToken);
    Task SendViralAlertAsync(TrackedAccount account, TrackedPost post, AlertSignal alert, CancellationToken cancellationToken);
}
