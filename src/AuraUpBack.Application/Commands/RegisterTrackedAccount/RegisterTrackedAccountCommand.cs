using AuraUpBack.Application.Contracts;
using AuraUpBack.Application.Mappers;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Repositories;

namespace AuraUpBack.Application.Commands.RegisterTrackedAccount;

public sealed record RegisterTrackedAccountCommand(
    string Handle,
    string MonitoringPrompt,
    bool MonitoringEnabled,
    int CheckEveryMinutes) : Abstractions.ICommand<TrackedAccountOverviewDto>;

internal sealed class RegisterTrackedAccountCommandHandler(ITrackedAccountRepository trackedAccountRepository)
    : Abstractions.ICommandHandler<RegisterTrackedAccountCommand, TrackedAccountOverviewDto>
{
    public async Task<TrackedAccountOverviewDto> HandleAsync(RegisterTrackedAccountCommand command, CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var account = await trackedAccountRepository.GetByHandleAsync(command.Handle, cancellationToken);

        if (account is null)
        {
            account = TrackedAccount.Create(
                command.Handle,
                command.MonitoringPrompt,
                command.MonitoringEnabled,
                command.CheckEveryMinutes,
                nowUtc);
        }
        else
        {
            account.ConfigureMonitoring(
                command.MonitoringPrompt,
                command.MonitoringEnabled,
                command.CheckEveryMinutes,
                nowUtc);
        }

        await trackedAccountRepository.UpsertAsync(account, cancellationToken);
        return account.ToOverviewDto();
    }
}
