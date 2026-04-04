using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class FileAlertSignalRepository(FileAuraUpBackStore store) : IAlertSignalRepository
{
    public Task<IReadOnlyCollection<AlertSignal>> GetLatestAsync(int take, CancellationToken cancellationToken)
    {
        return store.ReadAsync(
            snapshot => (IReadOnlyCollection<AlertSignal>)snapshot.Alerts
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(Math.Max(1, take))
                .ToList(),
            cancellationToken);
    }

    public Task AddAsync(AlertSignal signal, CancellationToken cancellationToken)
    {
        return store.WriteAsync(
            snapshot => snapshot.Alerts.Add(signal),
            cancellationToken);
    }

    public Task DeleteByAccountIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        return store.WriteAsync(
            snapshot => snapshot.Alerts.RemoveAll(x => x.AccountId == accountId),
            cancellationToken);
    }
}
