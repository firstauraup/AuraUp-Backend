using AuraUpBack.Domain.Enums;

namespace AuraUpBack.Application.Contracts;

public sealed record ExplorationRequestDto(
    Guid Id,
    string AccountHandle,
    string ResearchPrompt,
    ExplorationStatus Status,
    string Summary,
    DateTime CreatedAtUtc,
    DateTime? LastRunAtUtc,
    IReadOnlyCollection<string> SelectedPostExternalIds);
