using AuraUpBack.Domain.Enums;
using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Domain.Entities;

public sealed class TrackedAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AccountPlatform Platform { get; set; } = AccountPlatform.Instagram;
    public string Handle { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ProfileImageUrl { get; set; } = string.Empty;
    public string ProfileImageObjectKey { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public long FollowersCount { get; set; }
    public bool MonitoringEnabled { get; set; }
    public string MonitoringPrompt { get; set; } = string.Empty;
    public int CheckEveryMinutes { get; set; } = 60;
    public int OutlierNotificationMultiplier { get; set; } = 2;
    public string LastResearchSummary { get; set; } = string.Empty;
    public DateTime? LastInspectedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<TrackedPost> Posts { get; set; } = [];
    public List<AccountMetricSnapshot> MetricSnapshots { get; set; } = [];

    public static TrackedAccount Create(
        string handle,
        string monitoringPrompt,
        bool monitoringEnabled,
        int checkEveryMinutes,
        int outlierNotificationMultiplier,
        DateTime nowUtc)
    {
        return new TrackedAccount
        {
            Handle = NormalizeHandle(handle),
            MonitoringPrompt = monitoringPrompt.Trim(),
            MonitoringEnabled = monitoringEnabled,
            CheckEveryMinutes = Math.Max(1, checkEveryMinutes),
            OutlierNotificationMultiplier = NormalizeOutlierNotificationMultiplier(outlierNotificationMultiplier),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
    }

    public void ConfigureMonitoring(
        string monitoringPrompt,
        bool monitoringEnabled,
        int checkEveryMinutes,
        int outlierNotificationMultiplier,
        DateTime nowUtc)
    {
        MonitoringPrompt = monitoringPrompt.Trim();
        MonitoringEnabled = monitoringEnabled;
        CheckEveryMinutes = Math.Max(1, checkEveryMinutes);
        OutlierNotificationMultiplier = NormalizeOutlierNotificationMultiplier(outlierNotificationMultiplier);
        UpdatedAtUtc = nowUtc;
    }

    public void ApplyInspection(InspectionPayload payload, DateTime nowUtc)
    {
        Handle = NormalizeHandle(payload.Handle);
        DisplayName = payload.DisplayName.Trim();
        ProfileImageUrl = payload.ProfileImageUrl.Trim();
        Bio = payload.Bio.Trim();
        FollowersCount = payload.FollowersCount;
        LastResearchSummary = payload.ResearchSummary.Trim();
        LastInspectedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        CaptureMonthlyMetrics(nowUtc);

        var existingPostsByExternalId = Posts
            .ToDictionary(x => x.ExternalId, StringComparer.OrdinalIgnoreCase);

        foreach (var externalId in payload.SeenPostExternalIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existingPostsByExternalId.TryGetValue(externalId, out var existingPost))
            {
                existingPost.MarkSeen(nowUtc);
            }
        }

        foreach (var inspectedPost in payload.Posts)
        {
            if (!existingPostsByExternalId.TryGetValue(inspectedPost.ExternalId, out var post))
            {
                post = new TrackedPost
                {
                    ExternalId = inspectedPost.ExternalId
                };
                existingPostsByExternalId[inspectedPost.ExternalId] = post;
            }

            post.ApplyInspection(
                inspectedPost.IsReel,
                inspectedPost.Caption,
                inspectedPost.Url,
                inspectedPost.ThumbnailUrl,
                inspectedPost.PublishedAtUtc,
                inspectedPost.Views,
                inspectedPost.Likes,
                inspectedPost.Comments,
                inspectedPost.Shares,
                inspectedPost.IgPlayCount,
                inspectedPost.FbPlayCount,
                inspectedPost.FbLikes,
                inspectedPost.FbComments,
                inspectedPost.Topic,
                inspectedPost.TopicConfidence,
                inspectedPost.ContentAngle,
                inspectedPost.HookStyle,
                inspectedPost.ThemeSummary,
                nowUtc);
        }

        Posts = existingPostsByExternalId.Values.ToList();

        OutlierCalculator.Apply(Posts.Where(x => x.ShouldBeTreatedAsReel).ToList());
        Posts = Posts
            .OrderByDescending(x => x.PerformanceMultiplier)
            .ThenByDescending(x => x.Views)
            .ThenByDescending(x => x.PublishedAtUtc)
            .ToList();
    }

    public IEnumerable<TrackedPost> GetPostsWithin(DateTime? fromUtc, DateTime? toUtc)
    {
        var query = Posts.Where(x => x.ShouldBeTreatedAsReel);

        if (fromUtc.HasValue)
        {
            query = query.Where(x => x.PublishedAtUtc >= fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            query = query.Where(x => x.PublishedAtUtc <= toUtc.Value);
        }

        return query
            .OrderByDescending(x => x.PerformanceMultiplier)
            .ThenByDescending(x => x.Views)
            .ThenByDescending(x => x.PublishedAtUtc);
    }

    public IReadOnlyCollection<TrackedPost> GetReels()
    {
        return Posts
            .Where(x => x.ShouldBeTreatedAsReel)
            .OrderByDescending(x => x.PerformanceMultiplier)
            .ThenByDescending(x => x.Views)
            .ThenByDescending(x => x.PublishedAtUtc)
            .ToList();
    }

    private static string NormalizeHandle(string handle)
    {
        return handle.Trim().TrimStart('@').ToLowerInvariant();
    }

    private static int NormalizeOutlierNotificationMultiplier(int multiplier)
    {
        return Math.Clamp(multiplier, 2, 100);
    }

    private void CaptureMonthlyMetrics(DateTime nowUtc)
    {
        var snapshotMonthUtc = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var existingSnapshot = MetricSnapshots.FirstOrDefault(x => x.SnapshotMonthUtc == snapshotMonthUtc);

        if (existingSnapshot is null)
        {
            MetricSnapshots.Add(new AccountMetricSnapshot
            {
                AccountId = Id,
                SnapshotMonthUtc = snapshotMonthUtc,
                CapturedAtUtc = nowUtc,
                FollowersCount = FollowersCount,
            });

            MetricSnapshots = MetricSnapshots
                .OrderBy(x => x.SnapshotMonthUtc)
                .ToList();
            return;
        }

        existingSnapshot.CapturedAtUtc = nowUtc;
        existingSnapshot.FollowersCount = FollowersCount;
    }
}
