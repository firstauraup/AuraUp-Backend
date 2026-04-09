using AuraUpBack.Domain.Enums;

namespace AuraUpBack.Application.Contracts;

public sealed record ViralReelIdeaDto(
    Guid Id,
    int Rank,
    string Title,
    string Hook,
    string Premise,
    string Format,
    string WhyItCouldWork,
    string SourceReels,
    int Confidence,
    ViralIdeaClassification Classification);

public sealed record ViralIdeaGenerationResultDto(
    Guid BatchId,
    Guid AccountId,
    string AccountHandle,
    string Objective,
    int RequestedIdeaCount,
    int TotalIdeas,
    DateTime GeneratedAtUtc,
    IReadOnlyCollection<ViralReelIdeaDto> Ideas);
