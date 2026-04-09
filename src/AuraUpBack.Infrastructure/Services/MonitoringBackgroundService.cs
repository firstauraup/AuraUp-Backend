using AuraUpBack.Application.Abstractions;
using AuraUpBack.Domain.Enums;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Domain.Services;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Infrastructure.Services;

public sealed class MonitoringBackgroundService(
    ITrackedAccountRepository trackedAccountRepository,
    IInspectionJobQueue inspectionJobQueue,
    IInstagramConnectionAutomation instagramConnectionAutomation,
    IInstagramSettingsService instagramSettingsService,
    IOptions<InstagramIntegrationOptions> instagramOptions,
    IOptions<AuraUpBackStorageOptions> options,
    ILogger<MonitoringBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(10, options.Value.MonitoringLoopSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            if (ShouldPauseMonitoringForManualInstagram(instagramSettingsService.Current.Provider, instagramOptions.Value))
            {
                var instagramState = await instagramConnectionAutomation.GetStatusAsync(stoppingToken);
                if (instagramState.Status != InstagramConnectionStatus.Connected)
                {
                    logger.LogInformation(
                        "Monitoring paused while Instagram manual login is pending. Current status: {Status}",
                        instagramState.Status);
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }
            }

            var accounts = await trackedAccountRepository.GetAllAsync(stoppingToken);
            var nowUtc = DateTime.UtcNow;

            var dueAccounts = accounts
                .Where(x => x.MonitoringEnabled)
                .Where(account =>
                {
                    var cadenceMinutes = Math.Max(1, account.CheckEveryMinutes);
                    var latestJob = inspectionJobQueue.GetLatest(account.Id);
                    var lastAttemptAtUtc = GetLastAttemptAtUtc(account, latestJob);
                    var dueAtUtc = lastAttemptAtUtc.AddMinutes(cadenceMinutes);
                    return dueAtUtc <= nowUtc;
                })
                .OrderBy(x => x.Handle, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var account in dueAccounts)
            {
                try
                {
                    var latestJob = inspectionJobQueue.GetLatest(account.Id);
                    if (latestJob is not null &&
                        (latestJob.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
                         latestJob.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var job = inspectionJobQueue.Enqueue(account.Id, "Monitoring");
                    logger.LogInformation(
                        "Monitoreo encolado para @{Handle} con cadencia de {CadenceMinutes} minutos",
                        account.Handle,
                        Math.Max(1, account.CheckEveryMinutes));
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Fallo el monitoreo para @{Handle}", account.Handle);
                }
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private static bool ShouldPauseMonitoringForManualInstagram(string provider, InstagramIntegrationOptions options)
    {
        if (options.RpaHeadless)
        {
            return false;
        }

        return provider.Equals("Rpa", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime GetLastAttemptAtUtc(TrackedAccount account, InspectionJobStatus? latestJob)
    {
        var timestamps = new[]
        {
            account.LastInspectedAtUtc,
            account.CreatedAtUtc,
            latestJob?.QueuedAtUtc,
            latestJob?.StartedAtUtc,
            latestJob?.CompletedAtUtc,
        };

        return timestamps
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(DateTime.UtcNow)
            .Max();
    }
}
