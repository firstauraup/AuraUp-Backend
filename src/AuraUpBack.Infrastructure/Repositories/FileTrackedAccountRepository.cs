using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class FileTrackedAccountRepository(FileAuraUpBackStore store) : ITrackedAccountRepository
{
    public Task<IReadOnlyCollection<TrackedAccount>> GetAllAsync(CancellationToken cancellationToken)
    {
        return store.ReadAsync(
            snapshot => (IReadOnlyCollection<TrackedAccount>)snapshot.Accounts
                .OrderBy(x => x.Handle)
                .ToList(),
            cancellationToken);
    }

    public Task<TrackedAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return store.ReadAsync(
            snapshot => snapshot.Accounts.FirstOrDefault(x => x.Id == id),
            cancellationToken);
    }

    public Task<TrackedAccount?> GetByHandleAsync(string handle, CancellationToken cancellationToken)
    {
        var normalized = handle.Trim().TrimStart('@').ToLowerInvariant();

        return store.ReadAsync(
            snapshot => snapshot.Accounts.FirstOrDefault(x => x.Handle == normalized),
            cancellationToken);
    }

    public Task<TrackedAccount?> GetForMonitoringByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return GetByIdAsync(id, cancellationToken);
    }

    public Task<TrackedAccount?> GetForMonitoringByHandleAsync(string handle, CancellationToken cancellationToken)
    {
        return GetByHandleAsync(handle, cancellationToken);
    }

    public Task UpsertAsync(TrackedAccount account, CancellationToken cancellationToken)
    {
        return store.WriteAsync(
            snapshot =>
            {
                var index = snapshot.Accounts.FindIndex(x => x.Id == account.Id);
                if (index >= 0)
                {
                    snapshot.Accounts[index] = account;
                    return;
                }

                snapshot.Accounts.Add(account);
            },
            cancellationToken);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        return store.WriteAsync(
            snapshot => snapshot.Accounts.RemoveAll(x => x.Id == id),
            cancellationToken);
    }
}
