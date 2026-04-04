using AuraUpBack.Application.Abstractions;
using AuraUpBack.Infrastructure.Abstractions;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class InspectionProgressReporter(IInspectionJobQueue inspectionJobQueue) : IInspectionProgressReporter
{
    public void Report(
        Guid? jobId,
        string phase,
        string currentItem,
        int processedPosts,
        int discoveredPosts,
        int newPostsFound)
    {
        if (!jobId.HasValue)
        {
            return;
        }

        inspectionJobQueue.MarkProgress(
            jobId.Value,
            new InspectionJobProgress(
                phase,
                currentItem,
                processedPosts,
                discoveredPosts,
                newPostsFound));
    }
}
