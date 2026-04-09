namespace AuraUpBack.Domain.Services;

public interface IViralIdeaGenerationService
{
    Task<IReadOnlyCollection<ViralReelIdea>> GenerateIdeasAsync(
        ViralIdeaGenerationRequest request,
        CancellationToken cancellationToken);
}

public sealed record ViralIdeaGenerationRequest(
    string AccountHandle,
    string Objective,
    IReadOnlyCollection<ViralIdeaSourceReel> SourceReels);

public sealed record ViralIdeaSourceReel(
    string ExternalId,
    string Title,
    string Caption,
    string Transcript,
    long Views,
    long Likes,
    long Comments,
    long Shares,
    decimal PerformanceMultiplier,
    string Topic,
    string HookStyle,
    string ContentAngle,
    string ThemeSummary);

public sealed record ViralReelIdea(
    int Rank,
    string Title,
    string Hook,
    string Premise,
    string Format,
    string WhyItCouldWork,
    string SourceReels,
    int Confidence);
