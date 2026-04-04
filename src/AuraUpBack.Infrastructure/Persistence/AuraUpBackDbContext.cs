using System.Text.Json;
using AuraUpBack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace AuraUpBack.Infrastructure.Persistence;

internal sealed class AuraUpBackDbContext(DbContextOptions<AuraUpBackDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DbSet<TrackedAccount> TrackedAccounts => Set<TrackedAccount>();
    public DbSet<TrackedPost> TrackedPosts => Set<TrackedPost>();
    public DbSet<ExplorationRequest> ExplorationRequests => Set<ExplorationRequest>();
    public DbSet<AlertSignal> AlertSignals => Set<AlertSignal>();
    public DbSet<InstagramConnection> InstagramConnections => Set<InstagramConnection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var stringListConverter = new ValueConverter<List<string>, string>(
            value => JsonSerializer.Serialize(value ?? new List<string>(), JsonOptions),
            value => string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(value, JsonOptions) ?? new List<string>());
        var stringListComparer = new ValueComparer<List<string>>(
            (left, right) => (left ?? new List<string>()).SequenceEqual(right ?? new List<string>()),
            value => (value ?? new List<string>())
                .Aggregate(0, (current, item) => HashCode.Combine(current, item ?? string.Empty)),
            value => value == null ? new List<string>() : value.ToList());

        modelBuilder.Entity<TrackedAccount>(entity =>
        {
            entity.ToTable("TrackedAccounts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Handle).HasMaxLength(120).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(240);
            entity.Property(x => x.ProfileImageUrl).HasMaxLength(1_000);
            entity.Property(x => x.MonitoringPrompt).HasMaxLength(2_000);
            entity.Property(x => x.LastResearchSummary).HasMaxLength(8_000);
            entity.HasIndex(x => x.Handle).IsUnique();
            entity.HasMany(x => x.Posts)
                .WithOne()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TrackedPost>(entity =>
        {
            entity.ToTable("TrackedPosts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Caption).HasMaxLength(8_000);
            entity.Property(x => x.Url).HasMaxLength(1_000);
            entity.Property(x => x.ThumbnailUrl).HasMaxLength(1_000);
            entity.Property(x => x.Topic).HasMaxLength(120);
            entity.Property(x => x.ContentAngle).HasMaxLength(240);
            entity.Property(x => x.HookStyle).HasMaxLength(120);
            entity.Property(x => x.ThemeSummary).HasMaxLength(1_000);
            entity.Property(x => x.TopicConfidence).HasPrecision(5, 2);
            entity.Property(x => x.PerformanceMultiplier).HasPrecision(18, 4);
            entity.HasIndex(x => new { x.AccountId, x.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<ExplorationRequest>(entity =>
        {
            entity.ToTable("ExplorationRequests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AccountHandle).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ResearchPrompt).HasMaxLength(4_000);
            entity.Property(x => x.Summary).HasMaxLength(8_000);
            entity.Property(x => x.SelectedPostExternalIds)
                .HasConversion(stringListConverter)
                .Metadata.SetValueComparer(stringListComparer);
        });

        modelBuilder.Entity<AlertSignal>(entity =>
        {
            entity.ToTable("AlertSignals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExternalPostId).HasMaxLength(200);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.Property(x => x.Message).HasMaxLength(4_000);
            entity.Property(x => x.Severity).HasMaxLength(60);
            entity.HasIndex(x => x.AccountId);
            entity.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<InstagramConnection>(entity =>
        {
            entity.ToTable("InstagramConnections");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Username).HasMaxLength(240).IsRequired();
            entity.Property(x => x.EncryptedPassword).HasMaxLength(4_000);
            entity.Property(x => x.SessionStatePath).HasMaxLength(1_000);
            entity.Property(x => x.VerificationUrl).HasMaxLength(1_000);
            entity.Property(x => x.LastError).HasMaxLength(4_000);
            entity.HasIndex(x => x.UpdatedAtUtc);
        });
    }
}
