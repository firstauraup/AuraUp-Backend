using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class DbViralIdeaBatchRepository(IDbContextFactory<AuraUpBackDbContext> dbContextFactory) : IViralIdeaBatchRepository
{
    public async Task<IReadOnlyCollection<ViralIdeaBatch>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ViralIdeaBatches
            .AsNoTracking()
            .Include(x => x.Ideas.OrderBy(item => item.Rank))
            .OrderByDescending(x => x.GeneratedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<ViralIdeaBatch>> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ViralIdeaBatches
            .AsNoTracking()
            .Include(x => x.Ideas.OrderBy(item => item.Rank))
            .Where(x => x.AccountId == accountId)
            .OrderByDescending(x => x.GeneratedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<ViralIdeaBatch?> GetByIdAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ViralIdeaBatches
            .Include(x => x.Ideas.OrderBy(item => item.Rank))
            .FirstOrDefaultAsync(x => x.Id == batchId, cancellationToken);
    }

    public async Task UpsertAsync(ViralIdeaBatch batch, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.ViralIdeaBatches
            .Include(x => x.Ideas)
            .FirstOrDefaultAsync(x => x.Id == batch.Id, cancellationToken);

        if (existing is null)
        {
            foreach (var idea in batch.Ideas)
            {
                idea.BatchId = batch.Id;
            }

            dbContext.ViralIdeaBatches.Add(batch);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        dbContext.Entry(existing).CurrentValues.SetValues(batch);
        var incomingById = batch.Ideas.ToDictionary(x => x.Id);

        foreach (var currentIdea in existing.Ideas.ToList())
        {
            if (!incomingById.ContainsKey(currentIdea.Id))
            {
                dbContext.Remove(currentIdea);
            }
        }

        foreach (var incomingIdea in batch.Ideas)
        {
            incomingIdea.BatchId = existing.Id;

            var tracked = existing.Ideas.FirstOrDefault(item => item.Id == incomingIdea.Id);
            if (tracked is null)
            {
                existing.Ideas.Add(incomingIdea);
                dbContext.Entry(incomingIdea).State = EntityState.Added;
                continue;
            }

            dbContext.Entry(tracked).CurrentValues.SetValues(incomingIdea);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
