using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class DbApplicationFormSubmissionRepository(IDbContextFactory<AuraUpBackDbContext> dbContextFactory)
    : IApplicationFormSubmissionRepository
{
    public async Task AddAsync(ApplicationFormSubmission submission, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.ApplicationFormSubmissions.Add(submission);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
