namespace AuraUpBack.Application.Contracts;

public sealed record TranscriptionResultDto(
    Guid AccountId,
    Guid PostId,
    string Transcript);
