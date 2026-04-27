using AuraUpBack.Application.Abstractions;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Enums;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class EmailNotificationService(
    IEmailTemplateRenderer templateRenderer,
    IEmailSender emailSender,
    IAppUserRepository userRepository,
    IOptions<EmailOptions> options,
    ILogger<EmailNotificationService> logger) : IEmailNotificationService
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendApplicationFormSubmittedAsync(ApplicationFormSubmission submission, CancellationToken cancellationToken)
    {
        var recipients = await ResolveAdminRecipientsAsync(cancellationToken);
        var renderedEmail = templateRenderer.RenderApplicationFormSubmittedEmail(submission);

        await SendAsync(
            recipients,
            renderedEmail,
            [new EmailTag("type", "application_form")],
            cancellationToken);
    }

    public async Task SendInvitationAsync(string email, AppUserRole role, string invitationUrl, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        var renderedEmail = templateRenderer.RenderInvitationEmail(
            email,
            ToRoleLabel(role),
            invitationUrl,
            expiresAtUtc);

        await SendAsync(
            [email],
            renderedEmail,
            [new EmailTag("type", "user_invitation"), new EmailTag("role", role.ToString().ToLowerInvariant())],
            cancellationToken);
    }

    public async Task SendViralAlertAsync(TrackedAccount account, TrackedPost post, AlertSignal alert, CancellationToken cancellationToken)
    {
        var recipients = await ResolveAdminAndWorkerRecipientsAsync(cancellationToken);
        var renderedEmail = templateRenderer.RenderViralAlertEmail(account, post, alert);

        await SendAsync(
            recipients,
            renderedEmail,
            [new EmailTag("type", "viral_alert"), new EmailTag("severity", SanitizeTagValue(alert.Severity))],
            cancellationToken);
    }

    private async Task SendAsync(
        IReadOnlyCollection<string> recipients,
        RenderedEmail renderedEmail,
        IReadOnlyCollection<EmailTag> tags,
        CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            logger.LogWarning("Skipping email with subject {Subject} because no recipients were resolved.", renderedEmail.Subject);
            return;
        }

        await emailSender.SendAsync(
            new EmailMessage(recipients, renderedEmail.Subject, renderedEmail.Html, renderedEmail.Text, tags),
            cancellationToken);
    }

    private async Task<IReadOnlyCollection<string>> ResolveAdminRecipientsAsync(CancellationToken cancellationToken)
    {
        var users = await userRepository.GetAllAsync(cancellationToken);
        return ResolveRecipients(users.Where(x => x.Role == AppUserRole.Administrator));
    }

    private async Task<IReadOnlyCollection<string>> ResolveAdminAndWorkerRecipientsAsync(CancellationToken cancellationToken)
    {
        var users = await userRepository.GetAllAsync(cancellationToken);
        return ResolveRecipients(users.Where(x => x.Role is AppUserRole.Administrator or AppUserRole.Worker));
    }

    private IReadOnlyCollection<string> ResolveRecipients(IEnumerable<AppUser> users)
    {
        var recipients = users
            .Where(x => x.Status != AppUserStatus.Disabled)
            .Select(x => x.Email)
            .Concat(_options.AdminNotificationRecipients ?? [])
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Select(AppUser.NormalizeEmail)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return recipients;
    }

    private static string ToRoleLabel(AppUserRole role)
    {
        return role switch
        {
            AppUserRole.Client => "Cliente",
            AppUserRole.Worker => "Trabajador",
            AppUserRole.Administrator => "Administrador",
            _ => role.ToString()
        };
    }

    private static string SanitizeTagValue(string value)
    {
        return new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '_' or '-'
                ? character
                : '-')
            .ToArray());
    }
}
