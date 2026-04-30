using System.Net.Http.Json;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class ResendEmailSender(
    IHttpClientFactory httpClientFactory,
    IOptions<EmailOptions> options,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (message.To.Count == 0)
        {
            logger.LogInformation("Skipping email with subject {Subject} because there are no recipients.", message.Subject);
            return;
        }

        if (!_options.IsConfigured)
        {
            logger.LogWarning(
                "Skipping email with subject {Subject} because Email configuration is incomplete. Set Email:ApiKey and Email:From.",
                message.Subject);
            return;
        }

        var client = httpClientFactory.CreateClient(nameof(ResendEmailSender));
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new
            {
                from = _options.From,
                to = message.To,
                subject = message.Subject,
                html = message.Html,
                text = message.Text,
                reply_to = string.IsNullOrWhiteSpace(_options.ReplyTo) ? null : new[] { _options.ReplyTo.Trim() },
                tags = message.Tags?.Select(x => new { name = x.Name, value = x.Value }).ToArray(),
                headers = message.Headers
            })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
        request.Headers.UserAgent.ParseAdd("AuraUpBack/1.0");

        using var response = await client.SendAsync(request, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Resend rejected email with subject {Subject}. Status: {StatusCode}. Body: {Body}",
                message.Subject,
                (int)response.StatusCode,
                responseContent);
            throw new InvalidOperationException("The email provider rejected the email request.");
        }

        logger.LogInformation(
            "Email sent through Resend to {RecipientCount} recipients with subject {Subject}.",
            message.To.Count,
            message.Subject);
    }
}
