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
    private const int WorkerCount = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, WorkerCount)
            .Select(_ => RunWorkerAsync(stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            InspectionJobRequest? job = null;

            try
            {
                job = await inspectionJobQueue.DequeueAsync(stoppingToken);
                inspectionJobQueue.MarkRunning(job.JobId);

                await commandDispatcher.SendAsync(new InspectTrackedAccountCommand(job.AccountId, job.JobId), stoppingToken);
                inspectionJobQueue.MarkCompleted(job.JobId);
                logger.LogInformation("Inspection job {JobId} completed for account {AccountId}", job.JobId, job.AccountId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (job is not null)
                {
                    inspectionJobQueue.MarkFailed(job.JobId, exception.Message);
                    logger.LogError(exception, "Inspection job {JobId} failed for account {AccountId}", job.JobId, job.AccountId);
                    continue;
                }

                logger.LogError(exception, "Inspection worker failed before a job could be claimed.");
            }
        }
    }
}
