using AuraUpBack.Infrastructure.Abstractions;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class InMemoryInspectionJobQueue : IInspectionJobQueue
{
    private const string ManualSource = "Manual";
    private const string QueuedStatus = "Queued";
    private const string RunningStatus = "Running";
    private const string FailedStatus = "Failed";
    private const string CanceledPhase = "Canceled";
    private const string ManualCancellationReason = "Canceled because a manual inspection was requested.";

    private readonly SemaphoreSlim _signal = new(0);
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, InspectionJobRequest> _requestsByJobId = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellationSourcesByJobId = [];
    private readonly List<Guid> _manualQueue = [];
    private readonly List<Guid> _monitoringQueue = [];
    private readonly Dictionary<Guid, InspectionJobStatus> _latestByAccountId = [];
    public event Action<InspectionJobStatus>? StatusChanged;

    public InspectionJobStatus Enqueue(Guid accountId, string source)
    {
        var statusesToPublish = new List<InspectionJobStatus>();
        InspectionJobStatus? result = null;
        var shouldSignal = false;

        lock (_sync)
        {
            if (IsManual(source))
            {
                statusesToPublish.AddRange(CancelInterruptibleJobs(ManualCancellationReason));
            }

            if (_latestByAccountId.TryGetValue(accountId, out var existing) &&
                IsActive(existing))
            {
                result = existing;
            }
            else
            {
                var job = new InspectionJobRequest(Guid.NewGuid(), accountId, source, DateTime.UtcNow);
                var status = new InspectionJobStatus(
                    job.JobId,
                    job.AccountId,
                    job.Source,
                    QueuedStatus,
                    job.QueuedAtUtc,
                    null,
                    null,
                    string.Empty,
                    QueuedStatus,
                    string.Empty,
                    0,
                    0,
                    0,
                    []);

                _latestByAccountId[accountId] = status;
                _requestsByJobId[job.JobId] = job;
                _cancellationSourcesByJobId[job.JobId] = new CancellationTokenSource();
                EnqueueByPriority(job);
                statusesToPublish.Add(status);
                result = status;
                shouldSignal = true;
            }
        }

        foreach (var status in statusesToPublish)
        {
            StatusChanged?.Invoke(status);
        }

        if (shouldSignal)
        {
            _signal.Release();
        }

        return result ?? throw new InvalidOperationException("Inspection job could not be queued.");
    }

    public InspectionJobStatus? GetLatest(Guid accountId)
    {
        lock (_sync)
        {
            return _latestByAccountId.TryGetValue(accountId, out var status) ? status : null;
        }
    }

    public InspectionJobRequest? Claim(Guid jobId)
    {
        lock (_sync)
        {
            if (!_requestsByJobId.TryGetValue(jobId, out var request))
            {
                return null;
            }

            _requestsByJobId.Remove(jobId);
            _manualQueue.Remove(jobId);
            _monitoringQueue.Remove(jobId);
            return request;
        }
    }

    public CancellationTokenSource CreateLinkedCancellationTokenSource(Guid jobId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_cancellationSourcesByJobId.TryGetValue(jobId, out var jobCancellation))
            {
                jobCancellation = new CancellationTokenSource();
                _cancellationSourcesByJobId[jobId] = jobCancellation;
            }

            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, jobCancellation.Token);
        }
    }

    public bool IsCancellationRequested(Guid jobId)
    {
        lock (_sync)
        {
            return _cancellationSourcesByJobId.TryGetValue(jobId, out var cancellation) &&
                   cancellation.IsCancellationRequested;
        }
    }

    public ValueTask<InspectionJobRequest> DequeueAsync(CancellationToken cancellationToken)
    {
        return new ValueTask<InspectionJobRequest>(DequeueCoreAsync(cancellationToken));
    }

    public void MarkRunning(Guid jobId)
    {
        Update(jobId, status =>
        {
            if (!IsActive(status))
            {
                return status;
            }

            return status with
            {
                Status = RunningStatus,
                StartedAtUtc = DateTime.UtcNow,
                Error = string.Empty,
                CurrentPhase = "Starting"
            };
        });
    }

    public void MarkCompleted(Guid jobId)
    {
        Update(jobId, status => status with
        {
            Status = "Completed",
            CompletedAtUtc = DateTime.UtcNow,
            Error = string.Empty,
            CurrentPhase = "Completed"
        });
        DisposeCancellationSource(jobId);
    }

    public void MarkCanceled(Guid jobId, string reason)
    {
        Update(jobId, status => status with
        {
            Status = FailedStatus,
            CompletedAtUtc = DateTime.UtcNow,
            Error = reason,
            CurrentPhase = CanceledPhase,
            CurrentItem = string.Empty
        });
        DisposeCancellationSource(jobId);
    }

    public void MarkFailed(Guid jobId, string error)
    {
        Update(jobId, status => status with
        {
            Status = FailedStatus,
            CompletedAtUtc = DateTime.UtcNow,
            Error = error,
            CurrentPhase = "Failed"
        });
        DisposeCancellationSource(jobId);
    }

    public void MarkProgress(Guid jobId, InspectionJobProgress progress)
    {
        Update(jobId, status =>
        {
            if (!IsActive(status))
            {
                return status;
            }

            var recentItems = progress.RecentItems?.Where(x => !string.IsNullOrWhiteSpace(x)).TakeLast(20).ToList()
                ?? status.RecentItems.ToList();

            if (!string.IsNullOrWhiteSpace(progress.CurrentItem))
            {
                recentItems.Add(progress.CurrentItem.Trim());
                recentItems = recentItems
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .TakeLast(20)
                    .ToList();
            }

            return status with
            {
                CurrentPhase = progress.CurrentPhase,
                CurrentItem = progress.CurrentItem,
                ProcessedPosts = Math.Max(status.ProcessedPosts, progress.ProcessedPosts),
                DiscoveredPosts = Math.Max(status.DiscoveredPosts, progress.DiscoveredPosts),
                NewPostsFound = Math.Max(status.NewPostsFound, progress.NewPostsFound),
                RecentItems = recentItems
            };
        });
    }

    private void Update(Guid jobId, Func<InspectionJobStatus, InspectionJobStatus> update)
    {
        InspectionJobStatus? updatedStatus = null;

        lock (_sync)
        {
            var current = _latestByAccountId.Values.FirstOrDefault(x => x.JobId == jobId);
            if (current is null)
            {
                return;
            }

            updatedStatus = update(current);
            _latestByAccountId[current.AccountId] = updatedStatus;
        }

        if (updatedStatus is not null)
        {
            StatusChanged?.Invoke(updatedStatus);
        }
    }

    private async Task<InspectionJobRequest> DequeueCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken);

            lock (_sync)
            {
                if (TryDequeueNext(out var job))
                {
                    return job!;
                }
            }
        }
    }

    private bool TryDequeueNext(out InspectionJobRequest? job)
    {
        if (TryDequeueFrom(_manualQueue, out job))
        {
            return true;
        }

        return TryDequeueFrom(_monitoringQueue, out job);
    }

    private bool TryDequeueFrom(List<Guid> queue, out InspectionJobRequest? job)
    {
        while (queue.Count > 0)
        {
            var jobId = queue[0];
            queue.RemoveAt(0);

            if (_requestsByJobId.TryGetValue(jobId, out job))
            {
                _requestsByJobId.Remove(jobId);
                return true;
            }
        }

        job = null;
        return false;
    }

    private void EnqueueByPriority(InspectionJobRequest job)
    {
        if (IsManual(job.Source))
        {
            _manualQueue.Add(job.JobId);
            return;
        }

        _monitoringQueue.Add(job.JobId);
    }

    private List<InspectionJobStatus> CancelInterruptibleJobs(string reason)
    {
        var canceledStatuses = new List<InspectionJobStatus>();

        foreach (var status in _latestByAccountId.Values.ToList())
        {
            if (!CanInterrupt(status))
            {
                continue;
            }

            _manualQueue.Remove(status.JobId);
            _monitoringQueue.Remove(status.JobId);
            var wasStillQueued = _requestsByJobId.Remove(status.JobId);

            if (_cancellationSourcesByJobId.TryGetValue(status.JobId, out var cancellation))
            {
                cancellation.Cancel();
            }

            var canceledStatus = status with
            {
                Status = FailedStatus,
                CompletedAtUtc = DateTime.UtcNow,
                Error = reason,
                CurrentPhase = CanceledPhase,
                CurrentItem = string.Empty
            };

            _latestByAccountId[status.AccountId] = canceledStatus;
            canceledStatuses.Add(canceledStatus);

            if (wasStillQueued)
            {
                if (_cancellationSourcesByJobId.Remove(status.JobId, out var queuedCancellation))
                {
                    queuedCancellation.Dispose();
                }
            }
        }

        return canceledStatuses;
    }

    private static bool CanInterrupt(InspectionJobStatus status)
    {
        return !IsManual(status.Source) && IsActive(status);
    }

    private static bool IsActive(InspectionJobStatus status)
    {
        return status.Status.Equals(QueuedStatus, StringComparison.OrdinalIgnoreCase) ||
               status.Status.Equals(RunningStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManual(string source)
    {
        return source.Equals(ManualSource, StringComparison.OrdinalIgnoreCase);
    }

    private void DisposeCancellationSource(Guid jobId)
    {
        CancellationTokenSource? cancellation = null;

        lock (_sync)
        {
            if (_cancellationSourcesByJobId.Remove(jobId, out var existing))
            {
                cancellation = existing;
            }
        }

        cancellation?.Dispose();
    }
}
