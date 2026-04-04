namespace AuraUpBack.Domain.Entities;

public sealed class UserAccountAssignment
{
    public Guid UserId { get; set; }
    public Guid AccountId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
