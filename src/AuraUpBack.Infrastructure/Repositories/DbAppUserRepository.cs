using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class DbAppUserRepository(IDbContextFactory<AuraUpBackDbContext> dbContextFactory) : IAppUserRepository
{
    public async Task<IReadOnlyCollection<AppUser>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.AppUsers
            .AsNoTracking()
            .Include(x => x.AssignedAccounts)
            .OrderBy(x => x.Email)
            .ToListAsync(cancellationToken);
    }

    public async Task<AppUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.AppUsers
            .AsNoTracking()
            .Include(x => x.AssignedAccounts)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<AppUser?> GetByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = AppUser.NormalizeEmail(email);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.AppUsers
            .AsNoTracking()
            .Include(x => x.AssignedAccounts)
            .FirstOrDefaultAsync(x => x.Email == normalizedEmail, cancellationToken);
    }

    public async Task UpsertAsync(AppUser user, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.AppUsers
            .Include(x => x.AssignedAccounts)
            .FirstOrDefaultAsync(x => x.Id == user.Id, cancellationToken);

        if (existing is null)
        {
            dbContext.AppUsers.Add(user);
        }
        else
        {
            dbContext.Entry(existing).CurrentValues.SetValues(user);

            var incomingAssignments = user.AssignedAccounts
                .ToDictionary(x => x.AccountId, x => x);
            foreach (var existingAssignment in existing.AssignedAccounts.Where(x => !incomingAssignments.ContainsKey(x.AccountId)).ToList())
            {
                dbContext.UserAccountAssignments.Remove(existingAssignment);
            }

            foreach (var assignment in user.AssignedAccounts)
            {
                if (existing.AssignedAccounts.Any(x => x.AccountId == assignment.AccountId))
                {
                    continue;
                }

                existing.AssignedAccounts.Add(new UserAccountAssignment
                {
                    UserId = existing.Id,
                    AccountId = assignment.AccountId,
                    CreatedAtUtc = assignment.CreatedAtUtc
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.AppUsers
            .Include(x => x.AssignedAccounts)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (existing is null)
        {
            return;
        }

        var pendingInvitations = await dbContext.UserInvitations
            .Where(x => x.UserId == id)
            .ToListAsync(cancellationToken);
        dbContext.UserInvitations.RemoveRange(pendingInvitations);
        dbContext.UserAccountAssignments.RemoveRange(existing.AssignedAccounts);
        dbContext.AppUsers.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
