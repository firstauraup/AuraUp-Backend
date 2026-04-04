using AuraUpBack.Domain.Services;

namespace AuraUpBack.Domain.Entities;

public sealed class TrackedPost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string ThumbnailObjectKey { get; set; } = string.Empty;
    public DateTime PublishedAtUtc { get; set; }
    public long Views { get; set; }
    public long Likes { get; set; }
    public long Comments { get; set; }
    public long Shares { get; set; }
    public decimal PerformanceMultiplier { get; set; } = 1m;
    public bool IsOutlier { get; set; }
    public string PerformanceLabel { get; set; } = "baseline";
    public string? Transcript { get; set; }
    public string Topic { get; set; } = string.Empty;
    public decimal TopicConfidence { get; set; }
    public string ContentAngle { get; set; } = string.Empty;
    public string HookStyle { get; set; } = string.Empty;
    public string ThemeSummary { get; set; } = string.Empty;
    public bool IsReel { get; set; }
    public bool IsAnalyzed { get; set; }
    public DateTime? FirstSeenAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime? LastAnalyzedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool ShouldBeTreatedAsReel => IsReel || LooksLikeReelUrl(Url);

    public void ApplyInspection(
        bool isReel,
        string caption,
        string url,
        string thumbnailUrl,
        DateTime publishedAtUtc,
        long views,
        long likes,
        long comments,
        long shares,
        string topic,
        decimal topicConfidence,
        string contentAngle,
        string hookStyle,
        string themeSummary,
        DateTime nowUtc)
    {
        IsReel = isReel;
        Caption = caption;
        Url = url;
        ThumbnailUrl = thumbnailUrl;
        PublishedAtUtc = publishedAtUtc;
        Views = views;
        Likes = likes;
        Comments = comments;
        Shares = shares;
        Topic = topic;
        TopicConfidence = topicConfidence;
        ContentAngle = contentAngle;
        HookStyle = hookStyle;
        ThemeSummary = themeSummary;
        IsAnalyzed = true;
        FirstSeenAtUtc ??= nowUtc;
        LastSeenAtUtc = nowUtc;
        LastAnalyzedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkSeen(DateTime nowUtc)
    {
        FirstSeenAtUtc ??= nowUtc;
        LastSeenAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void SetPerformance(decimal multiplier, bool isOutlier)
    {
        PerformanceMultiplier = multiplier;
        IsOutlier = isOutlier;
        PerformanceLabel = $"x{multiplier:0.##}";
    }

    public void SetTranscript(string transcript, DateTime nowUtc)
    {
        Transcript = transcript;
        var classification = PostTopicClassifier.Classify(Caption, transcript);
        Topic = classification.Topic;
        TopicConfidence = classification.TopicConfidence;
        ContentAngle = classification.ContentAngle;
        HookStyle = classification.HookStyle;
        ThemeSummary = classification.ThemeSummary;
        UpdatedAtUtc = nowUtc;
    }

    public static bool LooksLikeReelUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("/reel/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/reels/", StringComparison.OrdinalIgnoreCase);
    }
}
