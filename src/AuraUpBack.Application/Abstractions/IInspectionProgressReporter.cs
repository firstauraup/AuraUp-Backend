namespace AuraUpBack.Application.Abstractions;

public interface IInspectionProgressReporter
{
    void Report(
        Guid? jobId,
        string phase,
        string currentItem,
        int processedPosts,
        int discoveredPosts,
        int newPostsFound);
}
