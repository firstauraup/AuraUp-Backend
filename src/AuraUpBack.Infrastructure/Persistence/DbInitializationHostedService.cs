using AuraUpBack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AuraUpBack.Infrastructure.Options;

namespace AuraUpBack.Infrastructure.Persistence;

internal sealed class DbInitializationHostedService(
    IDbContextFactory<AuraUpBackDbContext> dbContextFactory,
    FileAuraUpBackStore fileAuraUpBackStore,
    ILogger<DbInitializationHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureTrackedAccountsProfileImageColumnAsync(dbContext, cancellationToken);
        await EnsureTrackedPostsTopicColumnsAsync(dbContext, cancellationToken);
        await EnsureTrackedPostsIsReelColumnAsync(dbContext, cancellationToken);
        await EnsureTrackedPostsSharesColumnAsync(dbContext, cancellationToken);

        if (await HasAnyDataAsync(dbContext, cancellationToken))
        {
            return;
        }

        var snapshot = await fileAuraUpBackStore.ReadAsync(static value => value, cancellationToken);
        if (IsEmpty(snapshot))
        {
            return;
        }

        logger.LogInformation("Migrating legacy JSON snapshot into SQLite database.");

        foreach (var account in snapshot.Accounts)
        {
            foreach (var post in account.Posts)
            {
                post.AccountId = account.Id;
            }
        }

        dbContext.TrackedAccounts.AddRange(snapshot.Accounts);
        dbContext.ExplorationRequests.AddRange(snapshot.ExplorationRequests);
        dbContext.AlertSignals.AddRange(snapshot.Alerts);
        dbContext.InstagramConnections.AddRange(snapshot.InstagramConnections);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<bool> HasAnyDataAsync(AuraUpBackDbContext dbContext, CancellationToken cancellationToken)
    {
        return await dbContext.TrackedAccounts.AnyAsync(cancellationToken)
               || await dbContext.ExplorationRequests.AnyAsync(cancellationToken)
               || await dbContext.AlertSignals.AnyAsync(cancellationToken)
               || await dbContext.InstagramConnections.AnyAsync(cancellationToken);
    }

    private static bool IsEmpty(AuraUpBackSnapshot snapshot)
    {
        return snapshot.Accounts.Count == 0
               && snapshot.ExplorationRequests.Count == 0
               && snapshot.Alerts.Count == 0
               && snapshot.InstagramConnections.Count == 0;
    }

    private static async Task EnsureTrackedPostsTopicColumnsAsync(AuraUpBackDbContext dbContext, CancellationToken cancellationToken)
    {
        const string sql =
            """
            ALTER TABLE "TrackedPosts" ADD COLUMN IF NOT EXISTS "Topic" character varying(120) NOT NULL DEFAULT '';
            ALTER TABLE "TrackedPosts" ADD COLUMN IF NOT EXISTS "TopicConfidence" numeric(5,2) NOT NULL DEFAULT 0;
            ALTER TABLE "TrackedPosts" ADD COLUMN IF NOT EXISTS "ContentAngle" character varying(240) NOT NULL DEFAULT '';
            ALTER TABLE "TrackedPosts" ADD COLUMN IF NOT EXISTS "HookStyle" character varying(120) NOT NULL DEFAULT '';
            ALTER TABLE "TrackedPosts" ADD COLUMN IF NOT EXISTS "ThemeSummary" character varying(1000) NOT NULL DEFAULT '';
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureTrackedAccountsProfileImageColumnAsync(AuraUpBackDbContext dbContext, CancellationToken cancellationToken)
    {
        const string sql =
            """
            ALTER TABLE "TrackedAccounts" ADD COLUMN IF NOT EXISTS "ProfileImageUrl" character varying(1000) NOT NULL DEFAULT '';
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureTrackedPostsIsReelColumnAsync(AuraUpBackDbContext dbContext, CancellationToken cancellationToken)
    {
        const string sql =
            """
            ALTER TABLE "TrackedPosts" ADD COLUMN IF NOT EXISTS "IsReel" boolean NOT NULL DEFAULT FALSE;
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureTrackedPostsSharesColumnAsync(AuraUpBackDbContext dbContext, CancellationToken cancellationToken)
    {
        const string sql =
            """
            ALTER TABLE "TrackedPosts" ADD COLUMN IF NOT EXISTS "Shares" bigint NOT NULL DEFAULT 0;
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

}
