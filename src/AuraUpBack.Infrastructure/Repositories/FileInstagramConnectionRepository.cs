using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class FileInstagramConnectionRepository(FileAuraUpBackStore store) : IInstagramConnectionRepository
{
    public Task<InstagramConnection?> GetActiveAsync(CancellationToken cancellationToken)
    {
        return store.ReadAsync(
            snapshot => snapshot.InstagramConnections
                .OrderByDescending(x => x.UpdatedAtUtc)
                .FirstOrDefault(),
            cancellationToken);
    }

    public Task UpsertAsync(InstagramConnection connection, CancellationToken cancellationToken)
    {
        return store.WriteAsync(
            snapshot =>
            {
                var index = snapshot.InstagramConnections.FindIndex(x => x.Id == connection.Id);
                if (index >= 0)
                {
                    snapshot.InstagramConnections[index] = connection;
                    return;
                }

                snapshot.InstagramConnections.Add(connection);
            },
            cancellationToken);
    }
}
