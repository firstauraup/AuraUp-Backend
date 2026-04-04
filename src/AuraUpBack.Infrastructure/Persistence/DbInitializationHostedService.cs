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
        await EnsureTrackedAccountsMediaColumnsAsync(dbContext, cancellationToken);
        await EnsureTrackedPostsTopicColumnsAsync(dbContext, cancellationToken);
        await EnsureTrackedPostsMediaColumnsAsync(dbContext, cancellationToken);
        await EnsureTrackedPostsIsReelColumnAsync(dbContext, cancellationToken);
        await EnsureTrackedPostsSharesColumnAsync(dbContext, cancellationToken);
        await EnsureUserTablesAsync(dbContext, cancellationToken);

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

    private static async Task EnsureTrackedAccountsMediaColumnsAsync(AuraUpBackDbContext dbContext, CancellationToken cancellationToken)
    {
        const string sql =
            """
            ALTER TABLE "TrackedAccounts" ADD COLUMN IF NOT EXISTS "ProfileImageUrl" character varying(1000) NOT NULL DEFAULT '';
            ALTER TABLE "TrackedAccounts" ADD COLUMN IF NOT EXISTS "ProfileImageObjectKey" character varying(1000) NOT NULL DEFAULT '';
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureTrackedPostsMediaColumnsAsync(AuraUpBackDbContext dbContext, CancellationToken cancellationToken)
    {
        const string sql =
            """
            ALTER TABLE "TrackedPosts" ADD COLUMN IF NOT EXISTS "ThumbnailObjectKey" character varying(1000) NOT NULL DEFAULT '';
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

    private static async Task EnsureUserTablesAsync(AuraUpBackDbContext dbContext, CancellationToken cancellationToken)
    {
        const string sql =
            """
            CREATE TABLE IF NOT EXISTS "AppUsers" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Email" character varying(240) NOT NULL,
                "FirstName" character varying(120) NOT NULL DEFAULT '',
                "LastName" character varying(120) NOT NULL DEFAULT '',
                "PhoneNumber" character varying(60) NOT NULL DEFAULT '',
                "City" character varying(120) NOT NULL DEFAULT '',
                "Country" character varying(120) NOT NULL DEFAULT '',
                "CompanyName" character varying(180) NOT NULL DEFAULT '',
                "PasswordHash" character varying(1000) NOT NULL DEFAULT '',
                "Role" integer NOT NULL,
                "Status" integer NOT NULL,
                "LastLoginAtUtc" timestamp with time zone NULL,
                "ActivatedAtUtc" timestamp with time zone NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "UpdatedAtUtc" timestamp with time zone NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppUsers_Email" ON "AppUsers" ("Email");
            CREATE TABLE IF NOT EXISTS "UserInvitations" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "UserId" uuid NOT NULL,
                "Email" character varying(240) NOT NULL,
                "Role" integer NOT NULL,
                "TokenHash" character varying(256) NOT NULL,
                "ExpiresAtUtc" timestamp with time zone NOT NULL,
                "AcceptedAtUtc" timestamp with time zone NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserInvitations_TokenHash" ON "UserInvitations" ("TokenHash");
            CREATE TABLE IF NOT EXISTS "UserAccountAssignments" (
                "UserId" uuid NOT NULL,
                "AccountId" uuid NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                PRIMARY KEY ("UserId","AccountId")
            );
            CREATE INDEX IF NOT EXISTS "IX_UserAccountAssignments_AccountId" ON "UserAccountAssignments" ("AccountId");
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

}
