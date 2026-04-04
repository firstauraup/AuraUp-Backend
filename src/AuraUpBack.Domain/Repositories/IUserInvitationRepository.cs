using AuraUpBack.Domain.Entities;

namespace AuraUpBack.Domain.Repositories;

public interface IUserInvitationRepository
{
    Task<UserInvitation?> GetLatestActiveByEmailAsync(string email, CancellationToken cancellationToken);
    Task<UserInvitation?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);
    Task AddAsync(UserInvitation invitation, CancellationToken cancellationToken);
    Task UpdateAsync(UserInvitation invitation, CancellationToken cancellationToken);
}
