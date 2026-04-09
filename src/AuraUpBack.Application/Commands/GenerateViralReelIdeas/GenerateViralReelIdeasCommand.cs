using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Domain.Services;
using AuraUpBack.Domain.Enums;

namespace AuraUpBack.Application.Commands.GenerateViralReelIdeas;

public sealed record GenerateViralReelIdeasCommand(
    Guid AccountId,
    string Objective,
    IReadOnlyCollection<Guid> SelectedPostIds) : Abstractions.ICommand<ViralIdeaGenerationResultDto>;

internal sealed class GenerateViralReelIdeasCommandHandler(
    ITrackedAccountRepository trackedAccountRepository,
    IViralIdeaGenerationService viralIdeaGenerationService)
    : Abstractions.ICommandHandler<GenerateViralReelIdeasCommand, ViralIdeaGenerationResultDto>
{
    public async Task<ViralIdeaGenerationResultDto> HandleAsync(GenerateViralReelIdeasCommand command, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked account was not found.");

        var selectedPostIds = command.SelectedPostIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToHashSet();

        if (selectedPostIds.Count == 0)
        {
            throw new InvalidOperationException("Select at least one reel before generating ideas.");
        }

        var selectedReels = account.Posts
            .Where(post => selectedPostIds.Contains(post.Id))
            .OrderByDescending(post => post.PerformanceMultiplier)
            .ThenByDescending(post => post.Views)
            .ToList();

        if (selectedReels.Count == 0)
        {
            throw new InvalidOperationException("The selected reels were not found on this account.");
        }

        if (selectedReels.Any(post => string.IsNullOrWhiteSpace(post.Transcript)))
        {
            throw new InvalidOperationException("All selected reels must have a transcript before generating ideas.");
        }

        var ideas = await viralIdeaGenerationService.GenerateIdeasAsync(
            new ViralIdeaGenerationRequest(
                account.Handle,
                command.Objective,
                90,
                selectedReels.Select(post => new ViralIdeaSourceReel(
                    post.ExternalId,
                    BuildTitle(post.Caption, post.ExternalId),
                    post.Caption,
                    post.Transcript ?? string.Empty,
                    post.Views,
                    post.Likes,
                    post.Comments,
                    post.Shares,
                    post.PerformanceMultiplier,
                    post.Topic,
                    post.HookStyle,
                    post.ContentAngle,
                    post.ThemeSummary)).ToList()),
            cancellationToken);

        return new ViralIdeaGenerationResultDto(
            Guid.Empty,
            account.Id,
            account.Handle,
            command.Objective.Trim(),
            ideas.Count,
            ideas.Count,
            DateTime.UtcNow,
            ideas.Select(idea => new ViralReelIdeaDto(
                Guid.Empty,
                idea.Rank,
                idea.Title,
                idea.Hook,
                idea.Premise,
                idea.Format,
                idea.WhyItCouldWork,
                idea.SourceReels,
                idea.Confidence,
                ViralIdeaClassification.Unreviewed)).ToList());
    }

    private static string BuildTitle(string caption, string externalId)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return externalId;
        }

        var normalized = caption.Trim();
        return normalized.Length <= 72 ? normalized : $"{normalized[..72].TrimEnd()}...";
    }
}
