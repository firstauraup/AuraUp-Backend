using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class DbInstagramConnectionRepository(IDbContextFactory<AuraUpBackDbContext> dbContextFactory) : IInstagramConnectionRepository
{
    public async Task<InstagramConnection?> GetActiveAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.InstagramConnections
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task UpsertAsync(InstagramConnection connection, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.InstagramConnections.FirstOrDefaultAsync(x => x.Id == connection.Id, cancellationToken);

        if (existing is null)
        {
            dbContext.InstagramConnections.Add(connection);
        }
        else
        {
            dbContext.Entry(existing).CurrentValues.SetValues(connection);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
