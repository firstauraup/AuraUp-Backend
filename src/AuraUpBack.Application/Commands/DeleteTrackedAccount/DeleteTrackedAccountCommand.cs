using AuraUpBack.Domain.Repositories;

namespace AuraUpBack.Application.Commands.DeleteTrackedAccount;

public sealed record DeleteTrackedAccountCommand(Guid AccountId) : Abstractions.ICommand<bool>;

internal sealed class DeleteTrackedAccountCommandHandler(
    ITrackedAccountRepository trackedAccountRepository,
    IAlertSignalRepository alertSignalRepository,
    IExplorationRequestRepository explorationRequestRepository)
    : Abstractions.ICommandHandler<DeleteTrackedAccountCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteTrackedAccountCommand command, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(command.AccountId, cancellationToken);
        if (account is null)
        {
            return false;
        }

        await alertSignalRepository.DeleteByAccountIdAsync(account.Id, cancellationToken);
        await explorationRequestRepository.DeleteByAccountHandleAsync(account.Handle, cancellationToken);
        await trackedAccountRepository.DeleteAsync(account.Id, cancellationToken);

        return true;
    }
}
