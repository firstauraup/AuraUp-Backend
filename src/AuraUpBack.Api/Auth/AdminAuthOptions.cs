namespace AuraUpBack.Api.Auth;

public sealed class AdminAuthOptions
{
    public const string SectionName = "AdminAuth";

    public string Username { get; set; } = "admin";

    public string Email { get; set; } = "admin@auraup.local";

    public string Password { get; set; } = "ChangeMe123!";

    public string SigningKey { get; set; } = "change-this-signing-key-before-production";

    public int TokenLifetimeMinutes { get; set; } = 480;

    public int InvitationLifetimeHours { get; set; } = 72;

    public string PublicAppUrl { get; set; } = "https://www.auraup.org";
}
