using AuraUpBack.Domain.Enums;

namespace AuraUpBack.Domain.Entities;

public sealed class InstagramConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string EncryptedPassword { get; set; } = string.Empty;
    public string SessionStatePath { get; set; } = string.Empty;
    public string VerificationUrl { get; set; } = string.Empty;
    public InstagramConnectionStatus Status { get; set; } = InstagramConnectionStatus.Disconnected;
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime? LastValidatedAtUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool HasStoredCredentials => !string.IsNullOrWhiteSpace(EncryptedPassword);

    public static InstagramConnection Create(string username, string encryptedPassword, string sessionStatePath, DateTime nowUtc)
    {
        return new InstagramConnection
        {
            Username = NormalizeUsername(username),
            EncryptedPassword = encryptedPassword,
            SessionStatePath = sessionStatePath,
            Status = InstagramConnectionStatus.Disconnected,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
    }

    public void UpdateCredentials(string username, string encryptedPassword, string sessionStatePath, DateTime nowUtc)
    {
        Username = NormalizeUsername(username);
        EncryptedPassword = encryptedPassword;
        SessionStatePath = sessionStatePath;
        VerificationUrl = string.Empty;
        LastError = string.Empty;
        Status = InstagramConnectionStatus.Connecting;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkConnecting(DateTime nowUtc)
    {
        Status = InstagramConnectionStatus.Connecting;
        LastError = string.Empty;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkConnected(DateTime nowUtc)
    {
        Status = InstagramConnectionStatus.Connected;
        VerificationUrl = string.Empty;
        LastLoginAtUtc = nowUtc;
        LastValidatedAtUtc = nowUtc;
        LastError = string.Empty;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkValidated(DateTime nowUtc)
    {
        Status = InstagramConnectionStatus.Connected;
        VerificationUrl = string.Empty;
        LastValidatedAtUtc = nowUtc;
        LastError = string.Empty;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkVerificationRequired(string reason, string verificationUrl, DateTime nowUtc)
    {
        Status = InstagramConnectionStatus.VerificationRequired;
        VerificationUrl = verificationUrl.Trim();
        LastError = reason.Trim();
        UpdatedAtUtc = nowUtc;
    }

    public void MarkReconnectRequired(string reason, DateTime nowUtc)
    {
        Status = InstagramConnectionStatus.ReconnectRequired;
        VerificationUrl = string.Empty;
        LastError = reason.Trim();
        UpdatedAtUtc = nowUtc;
    }

    public void MarkFailed(string reason, DateTime nowUtc)
    {
        Status = InstagramConnectionStatus.Failed;
        VerificationUrl = string.Empty;
        LastError = reason.Trim();
        UpdatedAtUtc = nowUtc;
    }

    private static string NormalizeUsername(string username)
    {
        return username.Trim().TrimStart('@').ToLowerInvariant();
    }
}
