namespace AuraUpBack.Domain.Entities;

public sealed class AccountMetricSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public DateTime SnapshotMonthUtc { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public long FollowersCount { get; set; }
}
