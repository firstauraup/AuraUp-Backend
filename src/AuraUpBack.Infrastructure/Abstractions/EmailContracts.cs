namespace AuraUpBack.Infrastructure.Abstractions;

public interface IEmailTemplateRenderer
{
    RenderedEmail RenderApplicationFormSubmittedEmail(Domain.Entities.ApplicationFormSubmission submission);
    RenderedEmail RenderInvitationEmail(string email, string roleLabel, string invitationUrl, DateTime expiresAtUtc);
    RenderedEmail RenderViralAlertEmail(Domain.Entities.TrackedAccount account, Domain.Entities.TrackedPost post, Domain.Entities.AlertSignal alert);
}

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed record RenderedEmail(string Subject, string Html, string Text);

public sealed record EmailMessage(
    IReadOnlyCollection<string> To,
    string Subject,
    string Html,
    string Text,
    IReadOnlyCollection<EmailTag>? Tags = null);

public sealed record EmailTag(string Name, string Value);
