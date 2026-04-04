using AuraUpBack.Domain.Entities;

namespace AuraUpBack.Domain.Repositories;

public interface IAppUserRepository
{
    Task<IReadOnlyCollection<AppUser>> GetAllAsync(CancellationToken cancellationToken);
    Task<AppUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<AppUser?> GetByEmailAsync(string email, CancellationToken cancellationToken);
    Task UpsertAsync(AppUser user, CancellationToken cancellationToken);
}
