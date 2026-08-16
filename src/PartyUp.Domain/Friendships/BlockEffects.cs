using NodaTime;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Friendships;

/// <summary>
/// DOMAIN_MODEL §9: a participation that a block (§4) or a deletion execution (§11 step 3) turns
/// <c>Removed</c> takes its pending reminder with it. Scheduling lives above the domain, so the
/// cancellation leaves as an event rather than as a mutation.
/// </summary>
public sealed record SessionReminderCanceled(SessionId SessionId, UserId UserId) : IDomainEvent;

/// <summary>
/// What creating a block changed (DOMAIN_MODEL §4). Participations, poll participants and join
/// requests are already applied to the aggregates that were passed in; friendships are
/// <em>deleted</em> rather than flipped (D-105), so those rows leave as rows for the caller to
/// delete. Blocking notifies nobody (D-105) — the only event here is the reminder cancellation,
/// which is scheduling, not notification.
/// </summary>
public sealed record BlockEffectResult(
    IReadOnlyList<Friendship> DeletedFriendships,
    IReadOnlyList<SessionId> RemovedFromSessions,
    IReadOnlyList<PollId> RemovedFromPolls,
    IReadOnlyList<JoinRequestId> LapsedJoinRequests,
    IReadOnlyList<SessionReminderCanceled> CanceledReminders,
    IReadOnlyList<FriendGroupId> GroupsWithRemovedMembership)
{
    public static BlockEffectResult None { get; } = new([], [], [], [], [], []);
}

/// <summary>
/// The block/unblock effect service (DOMAIN_MODEL §4): pure orchestration over aggregates the
/// caller has already loaded and locked (§1). It owns no queries — which sessions, polls,
/// friendships to hand in is the application layer's job — and it is idempotent, so re-applying a
/// block that was already executed changes nothing.
/// </summary>
public static class BlockEffects
{
    /// <summary>
    /// §4: deletes the friendship in either status (D-065); where <em>either</em> party organizes a
    /// non-terminal, not-ended session or a live poll the other takes part in, the non-organizer's
    /// row becomes <c>Removed</c> — capacity freed, confirmation re-evaluated by the session itself,
    /// the pending reminder canceled (D-077/D-074, §9); and every open join request between the pair
    /// lapses, so the organizer can never approve a blocked counterpart. Completed rosters are frozen
    /// (D-095): in history the pair coexists, masked (D-076). Where <em>either</em> party owns a
    /// <see cref="FriendGroup"/> the other party <see cref="FriendGroup.Involves"/>, that membership
    /// row — <c>Invited</c> or <c>Accepted</c> — is deleted (D-202); groups where neither party owns
    /// are untouched (member↔member is mask-only, not a deletion, D-202/D-076).
    /// </summary>
    public static BlockEffectResult Apply(
        Block block,
        IEnumerable<Friendship> friendships,
        IEnumerable<Session> sessions,
        IEnumerable<Poll> polls,
        IReadOnlyCollection<FriendGroup> groups,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(friendships);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(polls);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();

        List<Friendship> deletedFriendships = [];
        List<SessionId> removedFromSessions = [];
        List<PollId> removedFromPolls = [];
        List<JoinRequestId> lapsedJoinRequests = [];
        List<SessionReminderCanceled> canceledReminders = [];
        List<FriendGroupId> groupsWithRemovedMembership = [];

        foreach (Friendship friendship in friendships)
        {
            if (friendship.IsBetween(block.BlockerId, block.BlockedId))
            {
                deletedFriendships.Add(friendship);
            }
        }

        foreach (Session session in sessions)
        {
            if (!TryCounterpartOf(block, session.OrganizerId, out UserId counterpart))
            {
                continue;
            }

            CollectPending(session.JoinRequests, counterpart, lapsedJoinRequests);
            session.LapseJoinRequestsFrom(counterpart, clock);

            // Not-ended is a clock evaluation, never the stored status alone (§1); an ended session
            // whose sweep is merely late must not be re-opened by a roster command.
            if (session.IsTerminal || now >= session.EndTime)
            {
                continue;
            }

            ParticipantRef participant = ParticipantRef.ForUser(counterpart);

            if (session.Find(participant) is not { IsRemoved: false })
            {
                continue;
            }

            session.RemoveParticipation(participant, clock);
            removedFromSessions.Add(session.Id);
            canceledReminders.Add(new SessionReminderCanceled(session.Id, counterpart));
        }

        foreach (Poll poll in polls)
        {
            if (!TryCounterpartOf(block, poll.OrganizerId, out UserId counterpart))
            {
                continue;
            }

            CollectPending(poll.JoinRequests, counterpart, lapsedJoinRequests);
            poll.LapseJoinRequestsFrom(counterpart, clock);

            if (poll.IsTerminal || now >= poll.SelectionWindowEndsAt)
            {
                continue;
            }

            ParticipantRef participant = ParticipantRef.ForUser(counterpart);

            if (poll.FindParticipant(participant) is not { IsRemoved: false })
            {
                continue;
            }

            poll.RemoveParticipant(participant, clock);
            removedFromPolls.Add(poll.Id);
        }

        foreach (FriendGroup group in groups)
        {
            if (!TryCounterpartOf(block, group.OwnerId, out UserId counterpart) || !group.Involves(counterpart))
            {
                continue;
            }

            group.RemoveCounterpartOf(group.OwnerId, counterpart);
            groupsWithRemovedMembership.Add(group.Id);
        }

        return new BlockEffectResult(
            deletedFriendships,
            removedFromSessions,
            removedFromPolls,
            lapsedJoinRequests,
            canceledReminders,
            groupsWithRemovedMembership);
    }

    /// <summary>
    /// §12-24: unblocking deletes the <see cref="Block"/> row and restores nothing — no friendship,
    /// and no resurrection of the <c>Removed</c> rows, which encode a removal that already happened.
    /// It only re-enables discovery and future interaction, so it has no aggregate effects at all.
    /// </summary>
    public static BlockEffectResult Unblock(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);

        return BlockEffectResult.None;
    }

    /// <summary>The pair member on the other side of an organizer; false when a third party organizes.</summary>
    private static bool TryCounterpartOf(Block block, UserId organizerId, out UserId counterpart)
    {
        if (organizerId == block.BlockerId)
        {
            counterpart = block.BlockedId;
            return true;
        }

        if (organizerId == block.BlockedId)
        {
            counterpart = block.BlockerId;
            return true;
        }

        counterpart = default;
        return false;
    }

    private static void CollectPending(
        IReadOnlyList<JoinRequest> requests,
        UserId requesterId,
        List<JoinRequestId> into)
    {
        foreach (JoinRequest request in requests)
        {
            if (request.RequesterId == requesterId && request.IsPending)
            {
                into.Add(request.Id);
            }
        }
    }
}
