using AuraUpBack.Domain.Models;

namespace AuraUpBack.Domain.Services;

public interface IInstagramConnectionAutomation
{
    Task<InstagramConnectionState> ConnectAsync(string username, string password, CancellationToken cancellationToken);
    Task<InstagramConnectionState> ReconnectAsync(CancellationToken cancellationToken);
    Task<InstagramConnectionState> VerifyCodeAsync(string code, CancellationToken cancellationToken);
    Task<InstagramConnectionState> EnsureConnectedAsync(CancellationToken cancellationToken);
    Task<InstagramConnectionState> GetStatusAsync(CancellationToken cancellationToken);
}
