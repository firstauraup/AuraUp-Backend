using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Infrastructure.Persistence;

namespace AuraUpBack.Infrastructure.Repositories;

internal sealed class FileTrackedAccountRepository(FileAuraUpBackStore store) : ITrackedAccountRepository
{
    public Task<IReadOnlyCollection<TrackedAccount>> GetAllAsync(CancellationToken cancellationToken)
    {
        return store.ReadAsync(
            snapshot => (IReadOnlyCollection<TrackedAccount>)snapshot.Accounts
                .OrderBy(x => x.Handle)
                .ToList(),
            cancellationToken);
    }

    public Task<TrackedAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return store.ReadAsync(
            snapshot => snapshot.Accounts.FirstOrDefault(x => x.Id == id),
            cancellationToken);
    }

    public Task<TrackedAccount?> GetByHandleAsync(string handle, CancellationToken cancellationToken)
    {
        var normalized = handle.Trim().TrimStart('@').ToLowerInvariant();

        return store.ReadAsync(
            snapshot => snapshot.Accounts.FirstOrDefault(x => x.Handle == normalized),
            cancellationToken);
    }

    public Task<TrackedAccount?> GetForMonitoringByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return GetByIdAsync(id, cancellationToken);
    }

    public Task<TrackedAccount?> GetForMonitoringByHandleAsync(string handle, CancellationToken cancellationToken)
    {
        return GetByHandleAsync(handle, cancellationToken);
    }

    public Task<TrackedPost?> GetPostByIdAsync(Guid accountId, Guid postId, CancellationToken cancellationToken)
    {
        return store.ReadAsync(
            snapshot => snapshot.Accounts
                .FirstOrDefault(x => x.Id == accountId)?
                .Posts
                .FirstOrDefault(x => x.Id == postId),
            cancellationToken);
    }

    public Task SetPostTranscriptAsync(
        Guid accountId,
        Guid postId,
        string transcript,
        string transcriptHook,
        string transcriptScript,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        return store.WriteAsync(
            snapshot =>
            {
                var post = snapshot.Accounts
                    .FirstOrDefault(x => x.Id == accountId)?
                    .Posts
                    .FirstOrDefault(x => x.Id == postId)
                    ?? throw new InvalidOperationException("Tracked post was not found.");

                post.SetTranscript(transcript, transcriptHook, transcriptScript, nowUtc);
            },
            cancellationToken);
    }

    public Task UpsertAsync(TrackedAccount account, CancellationToken cancellationToken)
    {
        return store.WriteAsync(
            snapshot =>
            {
                var index = snapshot.Accounts.FindIndex(x => x.Id == account.Id);
                if (index >= 0)
                {
                    PreserveExistingTranscripts(account, snapshot.Accounts[index]);
                    snapshot.Accounts[index] = account;
                    return;
                }

                snapshot.Accounts.Add(account);
            },
            cancellationToken);
    }

    private static void PreserveExistingTranscripts(TrackedAccount incomingAccount, TrackedAccount existingAccount)
    {
        var existingPostsByExternalId = existingAccount.Posts
            .ToDictionary(x => x.ExternalId, StringComparer.OrdinalIgnoreCase);

        foreach (var incomingPost in incomingAccount.Posts)
        {
            if (!existingPostsByExternalId.TryGetValue(incomingPost.ExternalId, out var existingPost))
            {
                continue;
            }

            PreserveExistingTranscript(incomingPost, existingPost);
        }
    }

    private static void PreserveExistingTranscript(TrackedPost incomingPost, TrackedPost existingPost)
    {
        if (!string.IsNullOrWhiteSpace(incomingPost.Transcript) ||
            string.IsNullOrWhiteSpace(existingPost.Transcript))
        {
            return;
        }

        incomingPost.Transcript = existingPost.Transcript;
        incomingPost.TranscriptHook = existingPost.TranscriptHook;
        incomingPost.TranscriptScript = existingPost.TranscriptScript;
        incomingPost.Topic = existingPost.Topic;
        incomingPost.TopicConfidence = existingPost.TopicConfidence;
        incomingPost.ContentAngle = existingPost.ContentAngle;
        incomingPost.HookStyle = existingPost.HookStyle;
        incomingPost.ThemeSummary = existingPost.ThemeSummary;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        return store.WriteAsync(
            snapshot => snapshot.Accounts.RemoveAll(x => x.Id == id),
            cancellationToken);
    }
}
