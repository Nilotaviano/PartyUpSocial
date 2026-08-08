using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The bulk, database-ordered multi-row lock port for poll commands, mirroring
/// <see cref="ISessionLockRepository"/> (A-068, PHASE6_DESIGN.md §2 "LOCKING"). Every set is locked
/// with a single <c>ORDER BY</c> statement so Postgres — never caller-computed C# order — decides
/// lock acquisition order.
/// </summary>
public interface IPollLockRepository
{
    /// <summary>Locks the given <c>users</c> rows in one statement, <c>ORDER BY id FOR NO KEY UPDATE</c> (A-069).</summary>
    Task LockUsersAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken);

    /// <summary>Locks the given <c>polls</c> rows in one statement, <c>ORDER BY id FOR NO KEY UPDATE</c> (A-069).</summary>
    Task LockPollsAsync(IReadOnlyCollection<PollId> pollIds, CancellationToken cancellationToken);
}
