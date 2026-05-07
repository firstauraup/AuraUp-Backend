using AuraUpBack.Domain.Entities;

namespace AuraUpBack.Domain.Repositories;

public interface ITrackedAccountRepository
{
    Task<IReadOnlyCollection<TrackedAccount>> GetAllAsync(CancellationToken cancellationToken);
    Task<TrackedAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<TrackedAccount?> GetByHandleAsync(string handle, CancellationToken cancellationToken);
    Task<TrackedAccount?> GetForMonitoringByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<TrackedAccount?> GetForMonitoringByHandleAsync(string handle, CancellationToken cancellationToken);
    Task<TrackedPost?> GetPostByIdAsync(Guid accountId, Guid postId, CancellationToken cancellationToken);
    Task SetPostTranscriptAsync(
        Guid accountId,
        Guid postId,
        string transcript,
        string transcriptHook,
        string transcriptScript,
        DateTime nowUtc,
        CancellationToken cancellationToken);
    Task UpsertAsync(TrackedAccount account, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
