using AuraUpBack.Domain.Enums;

namespace AuraUpBack.Domain.Models;

public sealed record InstagramConnectionState(
    string Provider,
    string Username,
    InstagramConnectionStatus Status,
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
