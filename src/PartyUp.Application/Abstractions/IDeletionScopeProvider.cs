using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// The deletion executor's scope port, widened for the §6.2 pre-lock/reload protocol: the caller must
/// discover every affected registered user with no locks held, lock that whole set in one
/// A-068-ordered statement, then reload the mutable scope under the normal graph lock order and
/// recompute the affected set from what was actually loaded. A changed set aborts the transaction
/// (PHASE4_DESIGN.md §6.2). <see cref="LoadAsync"/> is the pre-Phase-4 single-shot read (friendships and
/// blocks only, no session scope, no locks beyond its own raw friendship/block lock statements) and
/// stays available for callers that have not adopted the widened protocol.
/// </summary>
public interface IDeletionScopeProvider
{
    Task<DeletionScope> LoadAsync(UserId id, CancellationToken cancellationToken);

    /// <summary>
    /// The complete candidate registered-user set for <paramref name="id"/>'s deletion — the subject;
    /// every friendship/block counterpart; and every organizer, participant, and pending requester on a
    /// session deletion can cancel (the subject's own future sessions) or change (a session the subject
    /// merely participates in). Runs with **no locks held**, so the caller can lock the deduplicated set
    /// in one A-068-ordered statement before reloading anything mutable.
    /// </summary>
    Task<IReadOnlyCollection<UserId>> DiscoverAffectedUserIdsAsync(UserId id, CancellationToken cancellationToken) =>
        DefaultDiscoverAffectedUserIdsAsync(id, cancellationToken);

    /// <summary>
    /// Reloads the complete mutable scope after the caller has locked every discovered user row: locks
    /// friendships, then both block directions, then affected session parents — each in its own
    /// A-068-ordered statement — before loading their children. The default implementation degrades to
    /// <see cref="LoadAsync"/> for callers that have not adopted the widened protocol (it carries no
    /// session scope and no session-parent lock of its own).
    /// </summary>
    Task<DeletionScope> LoadForUpdateAsync(UserId id, CancellationToken cancellationToken) =>
        LoadAsync(id, cancellationToken);

    private async Task<IReadOnlyCollection<UserId>> DefaultDiscoverAffectedUserIdsAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        DeletionScope scope = await LoadAsync(id, cancellationToken);
        return ExtractAffectedUserIds(id, scope);
    }

    /// <summary>
    /// The shared extraction rule both the default fallback and the real Infrastructure discovery/recompute
    /// steps must agree on: for an owned session, every registered participant and pending requester is
    /// affected; for a session the subject merely participates in or has a pending request on, only the
    /// organizer is affected. Public so <c>ExecuteDeletionForUserUseCase</c> can recompute the same set
    /// from a freshly loaded <see cref="DeletionScope"/> without duplicating the rule.
    /// </summary>
    public static IReadOnlyCollection<UserId> ExtractAffectedUserIds(UserId id, DeletionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        HashSet<UserId> ids = [id];

        foreach (var friendship in scope.Friendships)
        {
            ids.Add(friendship.UserLoId);
            ids.Add(friendship.UserHiId);
        }

        foreach (var block in scope.Blocks)
        {
            ids.Add(block.BlockerId);
            ids.Add(block.BlockedId);
        }

        foreach (var session in scope.Sessions)
        {
            if (session.OrganizerId == id)
            {
                foreach (var participation in session.Participations)
                {
                    if (participation.UserId is { } userId)
                    {
                        ids.Add(userId);
                    }
                }

                foreach (var request in session.JoinRequests)
                {
                    if (request.Status == JoinRequestStatus.Pending)
                    {
                        ids.Add(request.RequesterId);
                    }
                }
            }
            else
            {
                ids.Add(session.OrganizerId);
            }
        }

        return ids;
    }
}
