using AuraUpBack.Domain.Entities;

namespace AuraUpBack.Domain.Repositories;

public interface IExplorationRequestRepository
{
    Task<IReadOnlyCollection<ExplorationRequest>> GetAllAsync(CancellationToken cancellationToken);
    Task<ExplorationRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task UpsertAsync(ExplorationRequest request, CancellationToken cancellationToken);
    Task DeleteByAccountHandleAsync(string accountHandle, CancellationToken cancellationToken);
}
