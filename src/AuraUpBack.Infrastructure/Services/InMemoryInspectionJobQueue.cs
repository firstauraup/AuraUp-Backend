using AuraUpBack.Infrastructure.Abstractions;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class InMemoryInspectionJobQueue : IInspectionJobQueue
{
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, InspectionJobRequest> _requestsByJobId = [];
    private readonly List<Guid> _manualQueue = [];
    private readonly List<Guid> _monitoringQueue = [];
    private readonly Dictionary<Guid, InspectionJobStatus> _latestByAccountId = [];
    public event Action<InspectionJobStatus>? StatusChanged;

    public InspectionJobStatus Enqueue(Guid accountId, string source)
    {
        InspectionJobStatus? queuedStatus = null;
        var shouldSignal = false;

        lock (_sync)
        {
            if (_latestByAccountId.TryGetValue(accountId, out var existing) &&
                (existing.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
                 existing.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)))
            {
                if (CanPromoteToManual(existing, source))
                {
                    PromoteToManual(existing.JobId);
                    queuedStatus = existing with
                    {
                        Source = "Manual"
                    };

                    _latestByAccountId[accountId] = queuedStatus;
                    _requestsByJobId[existing.JobId] = _requestsByJobId[existing.JobId] with
                    {
                        Source = "Manual"
                    };
                }

                return queuedStatus ?? existing;
            }

            var job = new InspectionJobRequest(Guid.NewGuid(), accountId, source, DateTime.UtcNow);
            var status = new InspectionJobStatus(
                job.JobId,
                job.AccountId,
                job.Source,
                "Queued",
                job.QueuedAtUtc,
                null,
                null,
                string.Empty,
                "Queued",
                string.Empty,
                0,
                0,
                0,
                []);

            _latestByAccountId[accountId] = status;
            _requestsByJobId[job.JobId] = job;
            EnqueueByPriority(job);
            queuedStatus = status;
            shouldSignal = true;
        }

        if (queuedStatus is not null)
        {
            StatusChanged?.Invoke(queuedStatus);
            if (shouldSignal)
            {
                _signal.Release();
            }

            return queuedStatus;
        }

        throw new InvalidOperationException("Inspection job could not be queued.");
    }

    public InspectionJobStatus? GetLatest(Guid accountId)
    {
        lock (_sync)
        {
            return _latestByAccountId.TryGetValue(accountId, out var status) ? status : null;
        }
    }

    public ValueTask<InspectionJobRequest> DequeueAsync(CancellationToken cancellationToken)
    {
        return new ValueTask<InspectionJobRequest>(DequeueCoreAsync(cancellationToken));
    }

    public void MarkRunning(Guid jobId)
    {
        Update(jobId, status => status with
        {
            Status = "Running",
            StartedAtUtc = DateTime.UtcNow,
            Error = string.Empty,
            CurrentPhase = "Starting"
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
    }

    public void MarkFailed(Guid jobId, string error)
    {
        Update(jobId, status => status with
        {
            Status = "Failed",
            CompletedAtUtc = DateTime.UtcNow,
            Error = error,
            CurrentPhase = "Failed"
        });
    }

    public void MarkProgress(Guid jobId, InspectionJobProgress progress)
    {
        Update(jobId, status =>
        {
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

    private void PromoteToManual(Guid jobId)
    {
        _monitoringQueue.Remove(jobId);

        if (!_manualQueue.Contains(jobId))
        {
            _manualQueue.Add(jobId);
        }
    }

    private static bool CanPromoteToManual(InspectionJobStatus existing, string requestedSource)
    {
        return existing.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase) &&
               IsManual(requestedSource) &&
               !IsManual(existing.Source);
    }

    private static bool IsManual(string source)
    {
        return source.Equals("Manual", StringComparison.OrdinalIgnoreCase);
    }
}
