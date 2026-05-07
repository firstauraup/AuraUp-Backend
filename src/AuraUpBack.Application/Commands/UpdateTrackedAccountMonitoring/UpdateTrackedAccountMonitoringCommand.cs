using AuraUpBack.Application.Contracts;
using AuraUpBack.Application.Mappers;
using AuraUpBack.Domain.Repositories;

namespace AuraUpBack.Application.Commands.UpdateTrackedAccountMonitoring;

public sealed record UpdateTrackedAccountMonitoringCommand(
    Guid AccountId,
    string MonitoringPrompt,
    bool MonitoringEnabled,
    int CheckEveryMinutes,
    int OutlierNotificationMultiplier) : Abstractions.ICommand<TrackedAccountOverviewDto>;

internal sealed class UpdateTrackedAccountMonitoringCommandHandler(ITrackedAccountRepository trackedAccountRepository)
    : Abstractions.ICommandHandler<UpdateTrackedAccountMonitoringCommand, TrackedAccountOverviewDto>
{
    public async Task<TrackedAccountOverviewDto> HandleAsync(UpdateTrackedAccountMonitoringCommand command, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetForMonitoringByIdAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked account was not found.");

        account.ConfigureMonitoring(
            command.MonitoringPrompt,
            command.MonitoringEnabled,
            command.CheckEveryMinutes,
            command.OutlierNotificationMultiplier,
            DateTime.UtcNow);

        await trackedAccountRepository.UpsertAsync(account, cancellationToken);
        return account.ToOverviewDto();
    }
}
