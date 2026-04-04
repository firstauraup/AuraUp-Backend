using AuraUpBack.Domain.Entities;

namespace AuraUpBack.Domain.Repositories;

public interface IInstagramConnectionRepository
{
    Task<InstagramConnection?> GetActiveAsync(CancellationToken cancellationToken);
    Task UpsertAsync(InstagramConnection connection, CancellationToken cancellationToken);
}
