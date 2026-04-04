using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class DbUserInvitationRepository(IDbContextFactory<AuraUpBackDbContext> dbContextFactory) : IUserInvitationRepository
{
    public async Task<UserInvitation?> GetLatestActiveByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = AppUser.NormalizeEmail(email);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.UserInvitations
            .AsNoTracking()
            .Where(x => x.Email == normalizedEmail && x.AcceptedAtUtc == null)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<UserInvitation?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.UserInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
    }

    public async Task AddAsync(UserInvitation invitation, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.UserInvitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(UserInvitation invitation, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.UserInvitations.Update(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
