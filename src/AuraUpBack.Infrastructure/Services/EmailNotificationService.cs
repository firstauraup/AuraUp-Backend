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
        var normalizedEmail = AppUser.NormalizeEmail(email);
        var roleLabel = role.ToString().ToLowerInvariant();

        var renderedEmail = templateRenderer.RenderInvitationEmail(
            normalizedEmail,
            roleLabel,
            invitationUrl,
            expiresAtUtc);

        await SendAsync(
            [normalizedEmail],
            renderedEmail,
            [new EmailTag("type", "user_invitation"), new EmailTag("role", roleLabel)],
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

        var headers = BuildDeliverabilityHeaders();

        await emailSender.SendAsync(
            new EmailMessage(recipients, renderedEmail.Subject, renderedEmail.Html, renderedEmail.Text, tags, headers),
            cancellationToken);
    }

    private IReadOnlyDictionary<string, string> BuildDeliverabilityHeaders()
    {
        var unsubscribeEmail = !string.IsNullOrWhiteSpace(_options.ReplyTo)
            ? _options.ReplyTo.Trim()
            : ExtractEmailAddress(_options.From);

        if (string.IsNullOrWhiteSpace(unsubscribeEmail))
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string>
        {
            ["List-Unsubscribe"] = $"<mailto:{unsubscribeEmail}>"
        };
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

    private static string ExtractEmailAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        var start = trimmed.LastIndexOf('<');
        var end = trimmed.LastIndexOf('>');

        if (start >= 0 && end > start)
        {
            return trimmed[(start + 1)..end].Trim();
        }

        return trimmed;
    }
}
