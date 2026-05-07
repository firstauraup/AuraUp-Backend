using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class DbAlertSignalRepository(IDbContextFactory<AuraUpBackDbContext> dbContextFactory) : IAlertSignalRepository
{
    public async Task<IReadOnlyCollection<AlertSignal>> GetLatestAsync(int take, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.AlertSignals
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(Math.Max(1, take))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(Guid accountId, string externalPostId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existingExternalIds = await dbContext.AlertSignals
            .AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .Select(x => x.ExternalPostId)
            .ToListAsync(cancellationToken);

        return existingExternalIds.Any(x => x.Equals(externalPostId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<int> GetHighestNotificationMultiplierAsync(Guid accountId, string externalPostId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var accountAlerts = await dbContext.AlertSignals
            .AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .Select(x => new
            {
                x.ExternalPostId,
                x.NotificationMultiplier
            })
            .ToListAsync(cancellationToken);

        return accountAlerts
            .Where(x => x.ExternalPostId.Equals(externalPostId, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.NotificationMultiplier)
            .DefaultIfEmpty(0)
            .Max();
    }

    public async Task AddAsync(AlertSignal signal, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.AlertSignals.Add(signal);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteByAccountIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var alerts = await dbContext.AlertSignals
            .Where(x => x.AccountId == accountId)
            .ToListAsync(cancellationToken);

        if (alerts.Count == 0)
        {
            return;
        }

        dbContext.AlertSignals.RemoveRange(alerts);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
