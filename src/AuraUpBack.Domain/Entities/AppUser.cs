using AuraUpBack.Domain.Enums;

namespace AuraUpBack.Domain.Entities;

public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public AppUserRole Role { get; set; } = AppUserRole.Client;
    public AppUserStatus Status { get; set; } = AppUserStatus.Invited;
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<UserAccountAssignment> AssignedAccounts { get; set; } = [];

    public static AppUser CreateInvited(string email, AppUserRole role, DateTime nowUtc)
    {
        return new AppUser
        {
            Email = NormalizeEmail(email),
            Role = role,
            Status = AppUserStatus.Invited,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
    }

    public void Activate(
        string firstName,
        string lastName,
        string phoneNumber,
        string city,
        string country,
        string companyName,
        string passwordHash,
        DateTime nowUtc)
    {
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        PhoneNumber = phoneNumber.Trim();
        City = city.Trim();
        Country = country.Trim();
        CompanyName = companyName.Trim();
        PasswordHash = passwordHash.Trim();
        Status = AppUserStatus.Active;
        ActivatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void RecordLogin(DateTime nowUtc)
    {
        LastLoginAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void UpdateAssignments(IEnumerable<Guid> accountIds, DateTime nowUtc)
    {
        var normalizedIds = accountIds.Distinct().ToHashSet();
        AssignedAccounts = normalizedIds
            .Select(accountId => new UserAccountAssignment
            {
                UserId = Id,
                AccountId = accountId,
                CreatedAtUtc = nowUtc
            })
            .ToList();
        UpdatedAtUtc = nowUtc;
    }

    public void Disable(DateTime nowUtc)
    {
        Status = AppUserStatus.Disabled;
        UpdatedAtUtc = nowUtc;
    }

    public bool IsAdministrator() => Role == AppUserRole.Administrator;
    public bool IsWorker() => Role == AppUserRole.Worker;
    public bool IsClient() => Role == AppUserRole.Client;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
