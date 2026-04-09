namespace AuraUpBack.Application.Contracts;

public sealed record ViralReelIdeaDto(
    int Rank,
    string Title,
    string Hook,
    string Premise,
    string Format,
    string WhyItCouldWork,
    string SourceReels,
    int Confidence);

public sealed record ViralIdeaGenerationResultDto(
    Guid AccountId,
    string AccountHandle,
    string Objective,
    int TotalIdeas,
    DateTime GeneratedAtUtc,
    IReadOnlyCollection<ViralReelIdeaDto> Ideas);
