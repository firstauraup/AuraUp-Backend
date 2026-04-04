using AuraUpBack.Domain.Enums;

namespace AuraUpBack.Domain.Entities;

public sealed class ExplorationRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AccountHandle { get; set; } = string.Empty;
    public string ResearchPrompt { get; set; } = string.Empty;
    public ExplorationStatus Status { get; set; } = ExplorationStatus.Pending;
    public string Summary { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAtUtc { get; set; }
    public List<string> SelectedPostExternalIds { get; set; } = [];

    public static ExplorationRequest Create(string accountHandle, string researchPrompt, IEnumerable<string>? selectedPostExternalIds, DateTime nowUtc)
    {
        return new ExplorationRequest
        {
            AccountHandle = accountHandle.Trim().TrimStart('@').ToLowerInvariant(),
            ResearchPrompt = researchPrompt.Trim(),
            CreatedAtUtc = nowUtc,
            SelectedPostExternalIds = selectedPostExternalIds?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? []
        };
    }

    public void MarkCompleted(string summary, DateTime nowUtc)
    {
        Status = ExplorationStatus.Completed;
        Summary = summary.Trim();
        LastRunAtUtc = nowUtc;
    }

    public void MarkFailed(string summary, DateTime nowUtc)
    {
        Status = ExplorationStatus.Failed;
        Summary = summary.Trim();
        LastRunAtUtc = nowUtc;
    }
}
