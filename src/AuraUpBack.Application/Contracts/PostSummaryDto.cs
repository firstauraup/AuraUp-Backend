namespace AuraUpBack.Application.Contracts;

public sealed record PostSummaryDto(
    Guid Id,
    string ExternalId,
    string Caption,
    string Url,
    string ThumbnailUrl,
    string ThumbnailObjectKey,
    DateTime PublishedAtUtc,
    long Views,
    long Likes,
    long Comments,
    long Shares,
    decimal PerformanceMultiplier,
    bool IsOutlier,
    string PerformanceLabel,
    string? Transcript,
    string Topic,
    decimal TopicConfidence,
    string ContentAngle,
    string HookStyle,
    string ThemeSummary);
