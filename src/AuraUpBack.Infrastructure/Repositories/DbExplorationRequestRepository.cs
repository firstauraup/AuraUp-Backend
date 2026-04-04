using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class DbExplorationRequestRepository(IDbContextFactory<AuraUpBackDbContext> dbContextFactory) : IExplorationRequestRepository
{
    public async Task<IReadOnlyCollection<ExplorationRequest>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ExplorationRequests
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<ExplorationRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ExplorationRequests.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task UpsertAsync(ExplorationRequest request, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.ExplorationRequests.FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);

        if (existing is null)
        {
            dbContext.ExplorationRequests.Add(request);
        }
        else
        {
            dbContext.Entry(existing).CurrentValues.SetValues(request);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteByAccountHandleAsync(string accountHandle, CancellationToken cancellationToken)
    {
        var normalizedHandle = accountHandle.Trim().TrimStart('@').ToLowerInvariant();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var requests = await dbContext.ExplorationRequests
            .Where(x => x.AccountHandle == normalizedHandle)
            .ToListAsync(cancellationToken);

        if (requests.Count == 0)
        {
            return;
        }

        dbContext.ExplorationRequests.RemoveRange(requests);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
