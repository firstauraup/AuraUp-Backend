namespace AuraUpBack.Domain.Models;

public sealed class InspectionPayload
{
    public string Handle { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ProfileImageUrl { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public long FollowersCount { get; set; }
    public string ResearchSummary { get; set; } = string.Empty;
    public List<string> SeenPostExternalIds { get; set; } = [];
    public List<InspectedPostPayload> Posts { get; set; } = [];
}

public sealed class InspectedPostPayload
{
    public bool IsReel { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public DateTime PublishedAtUtc { get; set; }
    public long Views { get; set; }
    public long Likes { get; set; }
    public long Comments { get; set; }
    public long Shares { get; set; }
    public string Topic { get; set; } = string.Empty;
    public decimal TopicConfidence { get; set; }
    public string ContentAngle { get; set; } = string.Empty;
    public string HookStyle { get; set; } = string.Empty;
    public string ThemeSummary { get; set; } = string.Empty;
}
