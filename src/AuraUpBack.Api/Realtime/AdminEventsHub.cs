using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AuraUpBack.Api.Realtime;

public sealed class AdminEventsHub(ILogger<AdminEventsHub> logger) : Hub
{
    public Task JoinAccount(Guid accountId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, BuildAccountGroup(accountId));
    }

    public Task LeaveAccount(Guid accountId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, BuildAccountGroup(accountId));
    }

    public static string BuildAccountGroup(Guid accountId)
    {
        return $"account:{accountId:N}";
    }

    public override Task OnConnectedAsync()
    {
        logger.LogInformation("Admin SignalR connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
        {
            logger.LogInformation("Admin SignalR disconnected: {ConnectionId}", Context.ConnectionId);
        }
        else
        {
            logger.LogWarning(exception, "Admin SignalR disconnected with error: {ConnectionId}", Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}
