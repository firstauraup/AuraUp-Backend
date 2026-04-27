namespace AuraUpBack.Application.Contracts;

public sealed record ApplicationFormSubmissionDto(
    Guid Id,
    string Email,
    string FullName,
    string CompanyName,
    string PrimaryNetwork,
    DateTime CreatedAtUtc);
