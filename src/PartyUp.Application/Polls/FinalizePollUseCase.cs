using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Polls;

public sealed record FinalizePollResult(bool Succeeded, string? Code, Poll? Poll, Session? Session)
{
    public static FinalizePollResult Ok(Poll poll, Session session) => new(true, null, poll, session);

    public static FinalizePollResult Failed(string code) => new(false, code, null, null);
}

/// <summary>
/// Organizer finalization (PHASE6_DESIGN.md §2 "Finalize", DOMAIN_MODEL §6.4): the clock-gated window
/// (due Close applied first, then <c>Deadline &lt;= now &lt; SelectionWindowEndsAt</c>) is entirely
/// the aggregate's own guard inside <see cref="Poll.Finalize"/> — this use case's job is the single
/// A-016 transaction around it: lock order per the organizer-only single-actor idiom (users, then the
/// poll parent — <see cref="PollOptionUseCases.DeleteOptionAsync"/>'s shape), persist the session
/// <see cref="Poll.Finalize"/> mints (<see cref="ISessionRepository.Add"/>), carry the poll's
/// <see cref="NotificationMute"/> rows onto the session (§12-15 — the re-parented guest links/identities
/// are already carried by the aggregate call itself, tracked-entity FK changes needing no separate
/// persistence step), materialize every event the finalization branch raised (option-selected in
/// every branch, §12-16), and replan the durable reminder handoff for every registered participant —
/// <see cref="ISessionReminderHandoffWriter.ReplanAsync"/> is itself the "only if still future, only if
/// Going/Maybe, only if not terminal" gate (§9), so this use case calls it uniformly rather than
/// re-deriving that gate.
/// </summary>
public sealed class FinalizePollUseCase(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    ISessionRepository sessions,
    IPollNotificationMaterializer materializer,
    ISessionReminderHandoffWriter reminders,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public Task<FinalizePollResult> FinalizeAsync(
        PollId pollId,
        UserId organizerId,
        PollOptionId optionId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                await pollLocks.LockUsersAsync([organizerId], ct);
                await pollLocks.LockPollsAsync([pollId], ct);

                PollForUpdate? forUpdate = await polls.LoadForUpdateAsync(pollId, ct);
                if (forUpdate is null)
                {
                    return FinalizePollResult.Failed("poll_not_found");
                }

                Poll poll = forUpdate.Poll;
                if (poll.OrganizerId != organizerId)
                {
                    return FinalizePollResult.Failed("poll_not_found");
                }

                // Ruling A (ticket-P6-S4a-fix1): mirrors Poll.Finalize's own unconditional window
                // check (Poll.cs:489-503) so the two windows surface distinct, honest codes instead
                // of collapsing into poll_voting_closed -- a transition attempted before the deadline
                // is not yet legal (poll_invalid_transition); one attempted from the 48-hour selection
                // window's close onward is the design-named poll_selection_window_closed. Both are
                // evaluated ahead of, and instead of, the domain call so its WindowClosed exception is
                // never produced for these two cases.
                Instant now = clock.GetCurrentInstant();
                if (now < poll.Deadline)
                {
                    return FinalizePollResult.Failed("poll_invalid_transition");
                }

                if (now >= poll.SelectionWindowEndsAt)
                {
                    return FinalizePollResult.Failed("poll_selection_window_closed");
                }

                // Ruling B (ticket-P6-S4a-fix1) / §12-12: a poll deleted down to zero options can only
                // be canceled or expire -- surface the honest poll_zero_options precondition instead
                // of the option lookup's generic poll_option_not_found. Guarded to the two non-terminal
                // statuses this branch can actually reach so an already-terminal poll (never reachable
                // with zero options once Finalized, but theoretically so once Canceled/Expired) still
                // reports through the domain call as poll_terminal.
                if (poll.Options.Count == 0 && poll.Status is PollStatus.Polling or PollStatus.Closed)
                {
                    return FinalizePollResult.Failed("poll_zero_options");
                }

                PollFinalizationResult result;
                try
                {
                    result = poll.Finalize(optionId, clock);
                }
                catch (DomainException exception)
                {
                    return FinalizePollResult.Failed(PollOptionUseCases.CodeFor(exception));
                }

                Session session = result.Session;
                sessions.Add(session);

                // Materialize before re-parenting the mutes: the materializer's own mute lookup
                // queries NotificationMutes by PollId on this same scoped context, and EF's identity
                // resolution would hand back the tracked (already re-parented) instances if the
                // AttachToSession loop ran first -- silently losing the poll-keyed mute mid-batch.
                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                // §12-15: the poll's mutes carry onto the session finalization created -- a
                // re-parent of the already-tracked row, not a delete-and-recreate, mirroring the
                // guest link/identity re-parent Poll.Finalize already performed on the aggregate.
                foreach (NotificationMute mute in forUpdate.Mutes)
                {
                    mute.AttachToSession(session.Id);
                }

                HashSet<UserId> reminderRecipients = [];
                foreach (Participation participation in session.Participations)
                {
                    if (participation.UserId is { } userId)
                    {
                        reminderRecipients.Add(userId);
                    }
                }

                foreach (UserId recipientId in reminderRecipients)
                {
                    await reminders.ReplanAsync(session, recipientId, ct);
                }

                return FinalizePollResult.Ok(poll, session);
            },
            cancellationToken);
}
