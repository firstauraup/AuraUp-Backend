using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class FileExplorationRequestRepository(FileAuraUpBackStore store) : IExplorationRequestRepository
{
    public Task<IReadOnlyCollection<ExplorationRequest>> GetAllAsync(CancellationToken cancellationToken)
    {
        return store.ReadAsync(
            snapshot => (IReadOnlyCollection<ExplorationRequest>)snapshot.ExplorationRequests
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToList(),
            cancellationToken);
    }

    public Task<ExplorationRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return store.ReadAsync(
            snapshot => snapshot.ExplorationRequests.FirstOrDefault(x => x.Id == id),
            cancellationToken);
    }

    public Task UpsertAsync(ExplorationRequest request, CancellationToken cancellationToken)
    {
        return store.WriteAsync(
            snapshot =>
            {
                var index = snapshot.ExplorationRequests.FindIndex(x => x.Id == request.Id);
                if (index >= 0)
                {
                    snapshot.ExplorationRequests[index] = request;
                    return;
                }

                snapshot.ExplorationRequests.Add(request);
            },
            cancellationToken);
    }

    public Task DeleteByAccountHandleAsync(string accountHandle, CancellationToken cancellationToken)
    {
        var normalizedHandle = accountHandle.Trim().TrimStart('@').ToLowerInvariant();

        return store.WriteAsync(
            snapshot => snapshot.ExplorationRequests.RemoveAll(x => x.AccountHandle == normalizedHandle),
            cancellationToken);
    }
}
