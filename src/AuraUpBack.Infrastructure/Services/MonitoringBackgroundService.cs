using AuraUpBack.Application.Abstractions;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Infrastructure.Services;

public sealed class MonitoringBackgroundService(
    ITrackedAccountRepository trackedAccountRepository,
    IInspectionJobQueue inspectionJobQueue,
    IOptions<AuraUpBackStorageOptions> options,
    ILogger<MonitoringBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(10, options.Value.MonitoringLoopSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
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

                    inspectionJobQueue.Enqueue(account.Id, "Monitoring");
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
