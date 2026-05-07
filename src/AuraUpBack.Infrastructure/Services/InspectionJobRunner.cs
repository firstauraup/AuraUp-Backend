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
        InspectionJobRequest? job = null;
        CancellationTokenSource? jobCancellation = null;
        var slotAcquired = false;

        try
        {
            job = inspectionJobQueue.Claim(jobId);
            if (job is null)
            {
                return;
            }

            jobCancellation = inspectionJobQueue.CreateLinkedCancellationTokenSource(job.JobId, CancellationToken.None);

            await _slots.WaitAsync(jobCancellation.Token);
            slotAcquired = true;
            jobCancellation.Token.ThrowIfCancellationRequested();

            inspectionJobQueue.MarkRunning(job.JobId);
            await commandDispatcher.SendAsync(new InspectTrackedAccountCommand(job.AccountId, job.JobId), jobCancellation.Token);
            inspectionJobQueue.MarkCompleted(job.JobId);
            _ = WarmMediaAsync(job.AccountId);
            logger.LogInformation("Inspection job {JobId} completed for account {AccountId}", job.JobId, job.AccountId);
        }
        catch (OperationCanceledException) when (job is not null &&
                                                 inspectionJobQueue.IsCancellationRequested(job.JobId))
        {
            inspectionJobQueue.MarkCanceled(job.JobId, "Inspection job was canceled by a manual request.");
            logger.LogInformation(
                "Inspection job {JobId} canceled so a manual inspection can run for account {AccountId}",
                job.JobId,
                job.AccountId);
        }
        catch (Exception exception)
        {
            if (job is not null)
            {
                inspectionJobQueue.MarkFailed(job.JobId, exception.Message);
                logger.LogError(exception, "Inspection job {JobId} failed for account {AccountId}", job.JobId, job.AccountId);
            }
            else
            {
                logger.LogError(exception, "Inspection job runner failed before job {JobId} could be claimed.", jobId);
            }
        }
        finally
        {
            if (slotAcquired)
            {
                _slots.Release();
            }

            jobCancellation?.Dispose();
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
