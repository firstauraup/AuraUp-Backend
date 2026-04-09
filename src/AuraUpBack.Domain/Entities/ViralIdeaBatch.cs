using AuraUpBack.Domain.Enums;

namespace AuraUpBack.Domain.Entities;

public sealed class ViralIdeaBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string AccountHandle { get; set; } = string.Empty;
    public string Objective { get; set; } = string.Empty;
    public int RequestedIdeaCount { get; set; }
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<ViralIdeaItem> Ideas { get; set; } = [];

    public static ViralIdeaBatch Create(
        Guid accountId,
        string accountHandle,
        string objective,
        int requestedIdeaCount,
        IReadOnlyCollection<Services.ViralReelIdea> ideas,
        DateTime nowUtc)
    {
        return new ViralIdeaBatch
        {
            AccountId = accountId,
            AccountHandle = accountHandle.Trim().TrimStart('@').ToLowerInvariant(),
            Objective = objective.Trim(),
            RequestedIdeaCount = Math.Max(1, requestedIdeaCount),
            GeneratedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            Ideas = ideas.Select(idea => ViralIdeaItem.Create(idea, nowUtc)).ToList()
        };
    }

    public void ClassifyIdea(Guid ideaId, ViralIdeaClassification classification, DateTime nowUtc)
    {
        var idea = Ideas.FirstOrDefault(item => item.Id == ideaId)
            ?? throw new InvalidOperationException("The idea was not found in this batch.");

        idea.SetClassification(classification, nowUtc);
        UpdatedAtUtc = nowUtc;
    }

    public int RemoveTrash(DateTime nowUtc)
    {
        var removed = Ideas.RemoveAll(item => item.Classification == ViralIdeaClassification.Trash);
        if (removed > 0)
        {
            UpdatedAtUtc = nowUtc;
        }

        return removed;
    }
}

public sealed class ViralIdeaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public int Rank { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Hook { get; set; } = string.Empty;
    public string Premise { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string WhyItCouldWork { get; set; } = string.Empty;
    public string SourceReels { get; set; } = string.Empty;
    public int Confidence { get; set; }
    public ViralIdeaClassification Classification { get; set; } = ViralIdeaClassification.Unreviewed;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public static ViralIdeaItem Create(Services.ViralReelIdea idea, DateTime nowUtc)
    {
        return new ViralIdeaItem
        {
            Rank = idea.Rank,
            Title = idea.Title.Trim(),
            Hook = idea.Hook.Trim(),
            Premise = idea.Premise.Trim(),
            Format = idea.Format.Trim(),
            WhyItCouldWork = idea.WhyItCouldWork.Trim(),
            SourceReels = idea.SourceReels.Trim(),
            Confidence = Math.Clamp(idea.Confidence, 1, 100),
            Classification = ViralIdeaClassification.Unreviewed,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
    }

    public void SetClassification(ViralIdeaClassification classification, DateTime nowUtc)
    {
        Classification = classification;
        UpdatedAtUtc = nowUtc;
    }
}
