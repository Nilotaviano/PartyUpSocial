using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Deletion;

public sealed class ExecuteDeletionForUserUseCase(
    IUserRepository users,
    IDeletionScopeProvider scopeProvider,
    IUserAccountGateway accounts,
    IDeletionJournal journal,
    IDeletionJournalShipper shipper,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<bool> ExecuteAsync(UserId userId, CancellationToken cancellationToken)
    {
        bool executed = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null || !user.IsPendingDeletion || !user.IsGraceElapsed(clock))
                {
                    return false;
                }

                DeletionScope scope = await scopeProvider.LoadAsync(userId, ct);
                DeletionExecutionResult result = DeletionExecutor.Execute(user, scope, clock);
                await accounts.PurgeCredentialsAsync(userId, ct);
                journal.Append(result.Journal);
                return true;
            },
            cancellationToken);

        if (executed)
        {
            await shipper.ShipPendingAsync(cancellationToken);
        }

        return executed;
    }
}
