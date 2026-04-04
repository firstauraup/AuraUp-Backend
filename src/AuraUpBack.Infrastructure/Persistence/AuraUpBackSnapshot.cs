using AuraUpBack.Domain.Entities;

namespace AuraUpBack.Infrastructure.Persistence;

internal sealed class AuraUpBackSnapshot
{
    public List<TrackedAccount> Accounts { get; set; } = [];
    public List<ExplorationRequest> ExplorationRequests { get; set; } = [];
    public List<AlertSignal> Alerts { get; set; } = [];
    public List<InstagramConnection> InstagramConnections { get; set; } = [];
}
