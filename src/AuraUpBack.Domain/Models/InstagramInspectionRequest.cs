namespace AuraUpBack.Domain.Models;

public sealed class InstagramInspectionRequest
{
    public string Handle { get; set; } = string.Empty;
    public string ResearchPrompt { get; set; } = string.Empty;
    public IReadOnlyCollection<string> KnownPostExternalIds { get; set; } = [];
    public int StartFromPostIndex { get; set; }
    public int DesiredNewPosts { get; set; }
    public int MaxDiscoveryPosts { get; set; }
    public int RefreshExistingPostsCount { get; set; }
    public Guid? JobId { get; set; }
    public bool ReconnectRetryAttempted { get; set; }
}
