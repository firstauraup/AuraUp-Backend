using AuraUpBack.Domain.Entities;

namespace AuraUpBack.Domain.Repositories;

public interface IViralIdeaBatchRepository
{
    Task<IReadOnlyCollection<ViralIdeaBatch>> GetAllAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ViralIdeaBatch>> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);
    Task<ViralIdeaBatch?> GetByIdAsync(Guid batchId, CancellationToken cancellationToken);
    Task UpsertAsync(ViralIdeaBatch batch, CancellationToken cancellationToken);
}
