namespace AuraUpBack.Infrastructure.Abstractions;

public interface IInspectionJobQueue
{
    event Action<InspectionJobStatus>? StatusChanged;
    InspectionJobStatus Enqueue(Guid accountId, string source);
    InspectionJobStatus? GetLatest(Guid accountId);
    InspectionJobRequest? Claim(Guid jobId);
    ValueTask<InspectionJobRequest> DequeueAsync(CancellationToken cancellationToken);
    void MarkRunning(Guid jobId);
    void MarkCompleted(Guid jobId);
    void MarkFailed(Guid jobId, string error);
    void MarkProgress(Guid jobId, InspectionJobProgress progress);
}

public sealed record InspectionJobRequest(Guid JobId, Guid AccountId, string Source, DateTime QueuedAtUtc);

public sealed record InspectionJobStatus(
    Guid JobId,
    Guid AccountId,
    string Source,
    string Status,
    DateTime QueuedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string Error,
    string CurrentPhase,
    string CurrentItem,
    int ProcessedPosts,
    int DiscoveredPosts,
    int NewPostsFound,
    IReadOnlyCollection<string> RecentItems);

public sealed record InspectionJobProgress(
    string CurrentPhase,
    string CurrentItem,
    int ProcessedPosts,
    int DiscoveredPosts,
    int NewPostsFound,
    IReadOnlyCollection<string>? RecentItems = null);
