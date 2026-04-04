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
