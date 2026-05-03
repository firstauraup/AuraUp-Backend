namespace AuraUpBack.Application.Contracts;

public sealed record TranscriptionResultDto(
    Guid AccountId,
    Guid PostId,
    string Transcript,
    string TranscriptHook,
    string TranscriptScript);

public sealed record ExternalReelTranscriptionDto(
    string ReelUrl,
    string Transcript,
    string TranscriptHook,
    string TranscriptScript);
