namespace AuraUpBack.Infrastructure.Options;

public sealed class InstagramIntegrationOptions
{
    public const string SectionName = "Instagram";

    public string Provider { get; set; } = "Mock";
    public string CredentialEncryptionKey { get; set; } = "change-this-instagram-credential-key";
    public string ApifyBaseUrl { get; set; } = "https://api.apify.com/v2";
    public string ApifyActorId { get; set; } = "apify~instagram-scraper";
    public string ApifyApiToken { get; set; } = string.Empty;
    public int ApifyRequestTimeoutSeconds { get; set; } = 180;

    public string RpaSessionStatePath { get; set; } = "App_Data/instagram-rpa-session.json";

    public bool RpaHeadless { get; set; } = true;

    public int RpaMaxPosts { get; set; } = 0;

    public int LoginTimeoutSeconds { get; set; } = 45;

    public int LoginTypingDelayMs { get; set; } = 120;

    public int ManualInterventionTimeoutSeconds { get; set; } = 300;

    public bool PreferStoredSession { get; set; } = true;

    public bool AllowPublicProfileReadWithoutSession { get; set; } = true;
}
