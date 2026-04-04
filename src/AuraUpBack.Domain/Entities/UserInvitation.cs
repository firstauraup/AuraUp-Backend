using AuraUpBack.Domain.Enums;

namespace AuraUpBack.Domain.Entities;

public sealed class UserInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public AppUserRole Role { get; set; } = AppUserRole.Client;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public static UserInvitation Create(Guid userId, string email, AppUserRole role, string tokenHash, DateTime expiresAtUtc, DateTime nowUtc)
    {
        return new UserInvitation
        {
            UserId = userId,
            Email = AppUser.NormalizeEmail(email),
            Role = role,
            TokenHash = tokenHash,
            ExpiresAtUtc = expiresAtUtc,
            CreatedAtUtc = nowUtc
        };
    }

    public bool CanBeAccepted(DateTime nowUtc)
    {
        return AcceptedAtUtc is null && ExpiresAtUtc > nowUtc;
    }

    public void MarkAccepted(DateTime nowUtc)
    {
        AcceptedAtUtc = nowUtc;
    }
}
