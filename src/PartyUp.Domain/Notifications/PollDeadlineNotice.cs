using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Domain.Notifications;

/// <summary>
/// "Deadline approaching" (§10, D-120) — a <em>scheduled</em> event rather than a transition, so it
/// has no domain-event record: the Phase 4 job asks these pure functions when to fire and whether
/// firing is still warranted. Recipients come from
/// <see cref="RecipientComputer.DeadlineApproaching(Poll, RecipientFilter, IClock)"/>.
/// </summary>
public static class PollDeadlineNotice
{
    /// <summary>D-120: one hour before the deadline.</summary>
    public static Duration LeadTime => Duration.FromHours(1);

    public static Instant NoticeInstantFor(Instant deadline) => deadline - LeadTime;

    public static Instant NoticeInstantFor(Poll poll)
    {
        ArgumentNullException.ThrowIfNull(poll);

        return NoticeInstantFor(poll.Deadline);
    }

    /// <summary>
    /// Whether the job should hold a notice for this poll at all. A poll that has already left
    /// <c>Polling</c>, or whose notice instant is behind us, is never scheduled — the same
    /// "no late firing" rule the reminder follows (§9).
    /// </summary>
    public static bool ShouldSchedule(Poll poll, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(clock);

        return poll.Status == PollStatus.Polling && NoticeInstantFor(poll) >= clock.GetCurrentInstant();
    }

    /// <summary>
    /// The skip predicate at firing time (§10): the notice is due, the deadline has not passed, and
    /// the poll is still <c>Polling</c> — a poll canceled, finalized or swept Closed in the meantime
    /// is skipped.
    /// </summary>
    public static bool ShouldSend(Poll poll, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();
        return poll.Status == PollStatus.Polling && now >= NoticeInstantFor(poll) && now < poll.Deadline;
    }
}
