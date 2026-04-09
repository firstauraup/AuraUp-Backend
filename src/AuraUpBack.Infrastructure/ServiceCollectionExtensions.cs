using AuraUpBack.Domain.Repositories;
using AuraUpBack.Domain.Services;
using AuraUpBack.Application.Abstractions;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using AuraUpBack.Infrastructure.Persistence;
using AuraUpBack.Infrastructure.Repositories;
using AuraUpBack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AuraUpBack.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, bool enableMonitoringService)
    {
        services.Configure<AuraUpBackStorageOptions>(configuration.GetSection(AuraUpBackStorageOptions.SectionName));
        services.Configure<InstagramIntegrationOptions>(configuration.GetSection(InstagramIntegrationOptions.SectionName));
        services.Configure<TranscriptionOptions>(configuration.GetSection(TranscriptionOptions.SectionName));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(MinioMediaOptions.FromConfiguration(configuration)));
        services.AddMemoryCache();

        services.AddDbContextFactory<AuraUpBackDbContext>((serviceProvider, optionsBuilder) =>
        {
            var hostEnvironment = serviceProvider.GetRequiredService<IHostEnvironment>();
            var connectionString = ResolveConnectionString(configuration, hostEnvironment);
            optionsBuilder.UseNpgsql(connectionString);
        });

        services.AddSingleton<FileAuraUpBackStore>();
        services.AddHostedService<DbInitializationHostedService>();
        services.AddSingleton<ITrackedAccountRepository, DbTrackedAccountRepository>();
        services.AddSingleton<IInstagramConnectionRepository, DbInstagramConnectionRepository>();
        services.AddSingleton<IExplorationRequestRepository, DbExplorationRequestRepository>();
        services.AddSingleton<IAlertSignalRepository, DbAlertSignalRepository>();
        services.AddSingleton<IAppUserRepository, DbAppUserRepository>();
        services.AddSingleton<IUserInvitationRepository, DbUserInvitationRepository>();
        services.AddSingleton<IInspectionJobQueue, InMemoryInspectionJobQueue>();
        services.AddSingleton<IInspectionProgressReporter, InspectionProgressReporter>();
        services.AddSingleton<InspectionJobRunner>();
        services.AddHostedService<InspectionJobBackgroundService>();
        services.AddHttpClient();
        services.AddSingleton<IInstagramCredentialVault, InstagramCredentialVault>();
        services.AddSingleton<IInstagramSettingsService, InstagramSettingsService>();
        services.AddSingleton<IMediaAssetStorage, MinioMediaStorage>();
        services.AddSingleton<InstagramBrowserProfileService>();
        services.AddSingleton<IInstagramConnectionAutomation, InstagramConnectionAutomation>();
        services.AddSingleton<IInstagramExplorerService, InstagramExplorerService>();
        services.AddSingleton<IInstagramInspectionProvider, MockInstagramInspectionProvider>();
        services.AddSingleton<IInstagramInspectionProvider, ApifyInstagramInspectionProvider>();
        services.AddSingleton<IInstagramInspectionProvider, RpaInstagramInspectionProvider>();
        services.AddSingleton<IInstagramResearchAutomation, InstagramResearchAutomation>();
        services.AddSingleton<IVideoTranscriptionService, ClipTranscribeVideoTranscriptionService>();

        if (enableMonitoringService)
        {
            services.AddHostedService<MonitoringBackgroundService>();
        }

        return services;
    }

    private static string ResolveConnectionString(IConfiguration configuration, IHostEnvironment hostEnvironment)
    {
        var configuredConnectionString = configuration.GetConnectionString("AuraUpBack");
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            var normalizedConnectionString = configuredConnectionString
                .Replace("{contentRoot}", hostEnvironment.ContentRootPath, StringComparison.OrdinalIgnoreCase);

            if (!normalizedConnectionString.StartsWith("jdbc:postgresql://", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedConnectionString;
            }

            return BuildNpgsqlConnectionString(normalizedConnectionString, configuration);
        }

        return BuildNpgsqlConnectionString("jdbc:postgresql://localhost:5432/auraUp-Db", configuration);
    }

    private static string BuildNpgsqlConnectionString(string jdbcUrl, IConfiguration configuration)
    {
        var jdbcPrefix = "jdbc:postgresql://";
        if (!jdbcUrl.StartsWith(jdbcPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The PostgreSQL JDBC URL must start with 'jdbc:postgresql://'.");
        }

        var uri = new Uri("http://" + jdbcUrl[jdbcPrefix.Length..]);
        var databaseName = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("The PostgreSQL JDBC URL must include the database name.");
        }

        var username = configuration["ConnectionStrings:AuraUpBackUsername"]
            ?? Environment.GetEnvironmentVariable("AURAUPBACK_DB_USERNAME")
            ?? Environment.GetEnvironmentVariable("POSTGRES_USER")
            ?? "postgres";

        var password = configuration["ConnectionStrings:AuraUpBackPassword"]
            ?? Environment.GetEnvironmentVariable("AURAUPBACK_DB_PASSWORD")
            ?? Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? "postgres";

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = databaseName,
            Username = username,
            Password = password
        };

        return builder.ConnectionString;
    }
}
