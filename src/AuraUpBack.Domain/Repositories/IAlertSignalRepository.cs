using AuraUpBack.Domain.Entities;

namespace AuraUpBack.Domain.Repositories;

public interface IAlertSignalRepository
{
    Task<IReadOnlyCollection<AlertSignal>> GetLatestAsync(int take, CancellationToken cancellationToken);
    Task AddAsync(AlertSignal signal, CancellationToken cancellationToken);
    Task DeleteByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);
}
