namespace AuraUpBack.Domain.Entities;

public sealed class AlertSignal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string ExternalPostId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "info";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
