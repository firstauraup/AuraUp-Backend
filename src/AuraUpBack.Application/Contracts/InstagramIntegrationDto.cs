namespace AuraUpBack.Application.Contracts;

public sealed record InstagramIntegrationDto(
    string Provider,
    string Username,
    string Status,
    bool HasStoredCredentials,
    string SessionStatePath,
    bool SessionStateExists,
    string VerificationUrl,
    DateTime? LastLoginAtUtc,
    DateTime? LastValidatedAtUtc,
    string LastError,
    bool Headless,
    int MaxPosts,
    bool AllowPublicProfileReadWithoutSession);
