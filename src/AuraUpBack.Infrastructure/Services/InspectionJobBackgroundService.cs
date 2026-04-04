using AuraUpBack.Application.Abstractions;
using AuraUpBack.Application.Commands.InspectTrackedAccount;
using AuraUpBack.Infrastructure.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class InspectionJobBackgroundService(
    IInspectionJobQueue inspectionJobQueue,
    ICommandDispatcher commandDispatcher,
    ILogger<InspectionJobBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await inspectionJobQueue.DequeueAsync(stoppingToken);
            inspectionJobQueue.MarkRunning(job.JobId);

            try
            {
                await commandDispatcher.SendAsync(new InspectTrackedAccountCommand(job.AccountId, job.JobId), stoppingToken);
                inspectionJobQueue.MarkCompleted(job.JobId);
                logger.LogInformation("Inspection job {JobId} completed for account {AccountId}", job.JobId, job.AccountId);
            }
            catch (Exception exception)
            {
                inspectionJobQueue.MarkFailed(job.JobId, exception.Message);
                logger.LogError(exception, "Inspection job {JobId} failed for account {AccountId}", job.JobId, job.AccountId);
            }
        }
    }
}
