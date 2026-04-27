namespace AuraUpBack.Infrastructure.Options;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string ApiKey { get; set; } = string.Empty;
    public string From { get; set; } = "AuraUp <onboarding@resend.dev>";
    public string ReplyTo { get; set; } = string.Empty;
    public string PublicAppUrl { get; set; } = "https://www.auraup.org";
    public string[] AdminNotificationRecipients { get; set; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(From);
}
