using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class DbTrackedAccountRepository(IDbContextFactory<AuraUpBackDbContext> dbContextFactory) : ITrackedAccountRepository
{
    public async Task<IReadOnlyCollection<TrackedAccount>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.TrackedAccounts
            .AsNoTracking()
            .Include(x => x.Posts)
            .OrderBy(x => x.Handle)
            .ToListAsync(cancellationToken);
    }

    public async Task<TrackedAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.TrackedAccounts
            .AsNoTracking()
            .Include(x => x.Posts)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<TrackedAccount?> GetByHandleAsync(string handle, CancellationToken cancellationToken)
    {
        var normalized = handle.Trim().TrimStart('@').ToLowerInvariant();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.TrackedAccounts
            .AsNoTracking()
            .Include(x => x.Posts)
            .FirstOrDefaultAsync(x => x.Handle == normalized, cancellationToken);
    }

    public async Task<TrackedAccount?> GetForMonitoringByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.TrackedAccounts
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<TrackedAccount?> GetForMonitoringByHandleAsync(string handle, CancellationToken cancellationToken)
    {
        var normalized = handle.Trim().TrimStart('@').ToLowerInvariant();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.TrackedAccounts
            .FirstOrDefaultAsync(x => x.Handle == normalized, cancellationToken);
    }

    public async Task UpsertAsync(TrackedAccount account, CancellationToken cancellationToken)
    {
        const int maxAttempts = 2;
        DbUpdateConcurrencyException? lastConcurrencyException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            try
            {
                var existing = await dbContext.TrackedAccounts
                    .Include(x => x.Posts)
                    .FirstOrDefaultAsync(x => x.Id == account.Id, cancellationToken);

                foreach (var post in account.Posts)
                {
                    post.AccountId = account.Id;
                }

                if (existing is null)
                {
                    dbContext.TrackedAccounts.Add(account);
                }
                else
                {
                    dbContext.Entry(existing).CurrentValues.SetValues(account);
                    var existingPostsByExternalId = existing.Posts
                        .ToDictionary(x => x.ExternalId, StringComparer.OrdinalIgnoreCase);
                    var incomingExternalIds = new HashSet<string>(
                        account.Posts.Select(x => x.ExternalId),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var existingPost in existing.Posts.Where(x => !incomingExternalIds.Contains(x.ExternalId)).ToList())
                    {
                        dbContext.TrackedPosts.Remove(existingPost);
                    }

                    foreach (var post in account.Posts)
                    {
                        if (existingPostsByExternalId.TryGetValue(post.ExternalId, out var trackedPost))
                        {
                            post.AccountId = existing.Id;
                            dbContext.Entry(trackedPost).CurrentValues.SetValues(post);
                            continue;
                        }

                        post.AccountId = existing.Id;
                        existing.Posts.Add(post);
                        dbContext.Entry(post).State = EntityState.Added;
                    }
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException exception) when (attempt < maxAttempts)
            {
                lastConcurrencyException = exception;
            }
            catch (DbUpdateConcurrencyException exception)
            {
                lastConcurrencyException = exception;
            }

        }

        throw lastConcurrencyException ?? new DbUpdateConcurrencyException("The tracked account could not be saved due to a concurrency conflict.");
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.TrackedAccounts.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (existing is null)
        {
            return;
        }

        dbContext.TrackedAccounts.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
