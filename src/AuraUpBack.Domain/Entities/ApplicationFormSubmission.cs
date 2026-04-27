namespace AuraUpBack.Domain.Entities;

public sealed class ApplicationFormSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string PrimaryNetwork { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public static ApplicationFormSubmission Create(
        string email,
        string phoneNumber,
        string fullName,
        string companyName,
        string primaryNetwork,
        DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryNetwork);

        return new ApplicationFormSubmission
        {
            Email = AppUser.NormalizeEmail(email),
            PhoneNumber = phoneNumber.Trim(),
            FullName = fullName.Trim(),
            CompanyName = companyName.Trim(),
            PrimaryNetwork = primaryNetwork.Trim(),
            CreatedAtUtc = nowUtc
        };
    }
}
