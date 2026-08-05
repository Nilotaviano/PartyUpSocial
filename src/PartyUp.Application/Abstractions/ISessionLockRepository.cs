using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The bulk, database-ordered multi-row lock port A-068 declares: every set is locked with a single
/// <c>ORDER BY</c> statement so Postgres — never caller-computed C# order — decides lock acquisition
/// order. Used by bulk invite/approval and other paths that must lock more than one row of a table in
/// the same statement (PHASE4_DESIGN.md §3 A-068, §6.1 <c>P4-LK-01</c> step 2).
/// </summary>
public interface ISessionLockRepository
{
    /// <summary>Locks the given <c>users</c> rows in one statement, <c>ORDER BY id FOR NO KEY UPDATE</c> (A-069).</summary>
    Task LockUsersAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken);

    /// <summary>Locks the given <c>sessions</c> rows in one statement, <c>ORDER BY id FOR NO KEY UPDATE</c> (A-069).</summary>
    Task LockSessionsAsync(IReadOnlyCollection<SessionId> sessionIds, CancellationToken cancellationToken);
}
