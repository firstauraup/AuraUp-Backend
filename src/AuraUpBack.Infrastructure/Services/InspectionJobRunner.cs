using System.Collections.Concurrent;
using AuraUpBack.Application.Abstractions;
using AuraUpBack.Application.Commands.InspectTrackedAccount;
using AuraUpBack.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace AuraUpBack.Infrastructure.Services;

public sealed class InspectionJobRunner(
    IInspectionJobQueue inspectionJobQueue,
    ICommandDispatcher commandDispatcher,
    IMediaAssetStorage mediaAssetStorage,
    ILogger<InspectionJobRunner> logger)
{
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly ConcurrentDictionary<Guid, byte> _scheduledJobs = new();

    public void Schedule(Guid jobId)
    {
        if (!_scheduledJobs.TryAdd(jobId, 0))
        {
            return;
        }

        _ = RunAsync(jobId);
    }

    private async Task RunAsync(Guid jobId)
    {
        try
        {
            var job = inspectionJobQueue.Claim(jobId);
            if (job is null)
            {
                return;
            }

            await _slots.WaitAsync();

            try
            {
                inspectionJobQueue.MarkRunning(job.JobId);
                await commandDispatcher.SendAsync(new InspectTrackedAccountCommand(job.AccountId, job.JobId), CancellationToken.None);
                inspectionJobQueue.MarkCompleted(job.JobId);
                _ = WarmMediaAsync(job.AccountId);
                logger.LogInformation("Inspection job {JobId} completed for account {AccountId}", job.JobId, job.AccountId);
            }
            catch (Exception exception)
            {
                inspectionJobQueue.MarkFailed(job.JobId, exception.Message);
                logger.LogError(exception, "Inspection job {JobId} failed for account {AccountId}", job.JobId, job.AccountId);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            _scheduledJobs.TryRemove(jobId, out _);
        }
    }

    private async Task WarmMediaAsync(Guid accountId)
    {
        try
        {
            await mediaAssetStorage.WarmAccountMediaAsync(accountId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Media warmup failed for account {AccountId}", accountId);
        }
    }
}
