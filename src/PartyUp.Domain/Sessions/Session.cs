using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Sessions;

/// <summary>
/// The fixed-time session aggregate (DOMAIN_MODEL §5). Its <see cref="Status"/> is materialized, not
/// re-derived: every command first applies any <em>due</em> time-based transition through the shared
/// idempotent functions and only then evaluates, so a logically-expired session can never be
/// canceled and a logically-completed one cancels as Completed (§1, D-156/D-157).
/// </summary>
/// <remarks>
/// Actor authorization (organizer-only, requester-only) belongs to the application layer: the
/// sweeper, the block executor and the deletion executor call these same functions directly (§1).
/// </remarks>
public sealed class Session : AggregateRoot
{
    private readonly List<Participation> _participations = [];
    private readonly List<JoinRequest> _joinRequests = [];
    private readonly List<GuestIdentity> _guestIdentities = [];
    private readonly List<GuestLink> _guestLinks = [];

    private Session()
    {
    }

    private Session(
        SessionId id,
        UserId organizerId,
        SessionDetails details,
        PollId? originPollId,
        SessionStatus status,
        Instant createdAt)
    {
        Id = id;
        OrganizerId = organizerId;
        Title = details.Title;
        StartTime = details.StartTime;
        Duration = details.Duration;
        Platform = details.Platform;
        Game = details.Game;
        Notes = details.Notes;
        MinAttendance = details.MinAttendance;
        MaxCapacity = details.MaxCapacity;
        IsPrivate = details.IsPrivate;
        OriginPollId = originPollId;
        Status = status;
        CreatedAt = createdAt;
    }

    public SessionId Id { get; private set; }

    /// <summary>D-075: permanent — ownership transfer is outside the initial release.</summary>
    public UserId OrganizerId { get; private set; }

    public SessionTitle Title { get; private set; }

    public Instant StartTime { get; private set; }

    public Duration15 Duration { get; private set; }

    public Platform Platform { get; private set; }

    /// <summary>§5.1: Steam catalog reference xor free text; every other platform is free text only.</summary>
    public SessionGame? Game { get; private set; }

    public SessionNotes Notes { get; private set; }

    public int MinAttendance { get; private set; }

    public int? MaxCapacity { get; private set; }

    public bool IsPrivate { get; private set; }

    /// <summary>Set by poll finalization — D-160's poll→session success linkage.</summary>
    public PollId? OriginPollId { get; private set; }

    public SessionStatus Status { get; private set; }

    public Instant CreatedAt { get; private set; }

    /// <summary>Derived from (start, duration), never stored (§1).</summary>
    public Instant EndTime => StartTime + Duration.Value;

    public TimeRange Window => new(StartTime, EndTime);

    public bool IsTerminal =>
        Status is SessionStatus.Completed or SessionStatus.Expired or SessionStatus.Canceled;

    /// <summary>D-056/D-164: only non-canceled Completed sessions stay in history.</summary>
    public bool IsRetainedInHistory => Status == SessionStatus.Completed;

    public IReadOnlyList<Participation> Participations => _participations;

    public IReadOnlyList<JoinRequest> JoinRequests => _joinRequests;

    public IReadOnlyList<GuestIdentity> GuestIdentities => _guestIdentities;

    public IReadOnlyList<GuestLink> GuestLinks => _guestLinks;

    /// <summary>D-062: only active Going counts, organizer and Going guests included.</summary>
    public int ActiveGoingCount => _participations.Count(p => p.IsActiveGoing);

    /// <summary>D-084: active Going + Maybe consume maximum capacity.</summary>
    public int ActiveOccupancy => _participations.Count(p => p.IsActiveConsumer);

    public int WaitlistCount => _participations.Count(p => p.IsWaitlisted);

    /// <summary>The D-074 grace: any Going-waitlisted candidate keeps a Confirmed session Confirmed.</summary>
    public bool HasGoingWaitlistedCandidate => _participations.Exists(p => p.IsGoingWaitlisted);

    /// <summary>D-091: with no maximum there is always room, so no waitlist can ever exist.</summary>
    public bool HasFreeSlot => MaxCapacity is not { } max || ActiveOccupancy < max;

    private int FreeSlots => MaxCapacity is { } max ? Math.Max(0, max - ActiveOccupancy) : int.MaxValue;

    /// <summary>"In progress" is a clock evaluation, not a status (§5.1).</summary>
    public bool IsInProgress(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();
        return now >= StartTime && now < EndTime;
    }

    /// <summary>
    /// Direct creation (spec §7.1). The start is validated against the D-130 window, the organizer
    /// starts Going (D-075), and a non-private session needs at least one directly invited
    /// registered friend (D-099) — friendship itself is an application-layer check. A private
    /// session may be created with guest links only (D-125), hence an empty invitee set.
    /// </summary>
    public static Session Create(
        UserId organizerId,
        SessionDetails details,
        IReadOnlyCollection<UserId> invitees,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(invitees);
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();
        SessionStartWindow.Require(details.StartTime, now);

        if (!details.IsPrivate && invitees.Count == 0)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                "A non-private session requires at least one directly invited registered friend at creation (D-099).");
        }

        Session session = new(SessionId.New(), organizerId, details, null, SessionStatus.Unconfirmed, now);
        session._participations.Add(Participation.Organizer(session.Id, organizerId, now));

        foreach (UserId invitee in invitees)
        {
            if (invitee == organizerId)
            {
                throw new DomainException(DomainError.Conflict, "The organizer is already on the roster.");
            }

            if (session._participations.Exists(p => p.Matches(ParticipantRef.ForUser(invitee))))
            {
                throw new DomainException(DomainError.Duplicate, "That user has already been invited.");
            }

            session._participations.Add(Participation.Invited(session.Id, ParticipantRef.ForUser(invitee), now));
            session.Raise(new SessionInvitationSent(session.Id, invitee));
        }

        session.ReevaluateConfirmation(clock);
        return session;
    }

    /// <summary>
    /// D-153/§6.4: poll finalization mints the session directly in <c>Unconfirmed</c>,
    /// <c>Confirmed</c>, <c>Completed</c> or <c>Expired</c> — the only path to Completed without a
    /// prior Confirmed — from an already-mapped and already-allocated roster. The D-130 window does
    /// not apply: the selected option's instant may lie in the past. Sweeper re-runs over such rows
    /// are no-ops.
    /// </summary>
    public static Session FromPollFinalization(
        PollId originPollId,
        UserId organizerId,
        SessionDetails details,
        IEnumerable<FinalizedParticipation> roster,
        SessionStatus initialStatus,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(clock);

        if (initialStatus is not (SessionStatus.Unconfirmed or SessionStatus.Confirmed
            or SessionStatus.Completed or SessionStatus.Expired))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Finalization cannot create a session in '{initialStatus}' (D-153)."));
        }

        Instant now = clock.GetCurrentInstant();
        Session session = new(SessionId.New(), organizerId, details, originPollId, initialStatus, now);

        foreach (FinalizedParticipation entry in roster)
        {
            if (entry.Status is ParticipationStatus.Removed)
            {
                throw new DomainException(
                    DomainError.ValidationFailed,
                    "Removed poll participants are excluded from the finalization mapping (§6.3).");
            }

            if (entry.IsWaitlisted && entry.Status is not (ParticipationStatus.Going or ParticipationStatus.Maybe))
            {
                throw new DomainException(
                    DomainError.ValidationFailed,
                    "Only a Going or Maybe row can be waitlisted (D-084).");
            }

            if (session._participations.Exists(p => p.Matches(entry.Participant)))
            {
                throw new DomainException(DomainError.Duplicate, "One participation per participant per session.");
            }

            session._participations.Add(Participation.FromPollFinalization(
                session.Id, entry.Participant, entry.Status, entry.IsWaitlisted, now));
        }

        ParticipantRef organizer = ParticipantRef.ForUser(organizerId);
        if (!session._participations.Exists(p => p.Matches(organizer)))
        {
            // D-155: the organizer becomes Going regardless of their vote, and is allocated first (D-119).
            session._participations.Insert(
                0,
                Participation.FromPollFinalization(
                    session.Id, organizer, ParticipationStatus.Going, isWaitlisted: false, now));
        }

        if (session.MaxCapacity is { } max && session.ActiveOccupancy > max)
        {
            throw new DomainException(
                DomainError.CapacityExceeded,
                FormattableString.Invariant(
                    $"The finalized roster puts {session.ActiveOccupancy} active participants into {max} slots (D-119)."));
        }

        return session;
    }

    /// <summary>§6.4-6: the poll's guests carry onto the created session.</summary>
    public void AdoptGuestIdentity(GuestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        identity.AttachToSession(Id);
        _guestIdentities.Add(identity);
    }

    /// <summary>§6.4-6: with the token preserved — D-048 forbids re-issuing a guest link.</summary>
    public void AdoptGuestLink(GuestLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        link.AttachToSession(Id);
        _guestLinks.Add(link);
    }

    // ---- §5.5 state machine -------------------------------------------------------------------

    /// <summary>
    /// Applies any transition the clock has already made due, ahead of evaluating a command (§1).
    /// Idempotent, and the sweeper's own entry point.
    /// </summary>
    public void ApplyDueTransitions(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (clock.GetCurrentInstant() < EndTime)
        {
            return;
        }

        switch (Status)
        {
            case SessionStatus.Confirmed:
                Complete(clock);
                break;
            case SessionStatus.Unconfirmed:
                Expire(clock);
                break;
            default:
                break;
        }
    }

    /// <summary>D-053/D-106: active Going reaches the minimum, and the session has not yet ended.</summary>
    public void Confirm(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status == SessionStatus.Confirmed)
        {
            return;
        }

        RequireStatus(SessionStatus.Unconfirmed, "confirm");

        if (clock.GetCurrentInstant() >= EndTime)
        {
            throw new DomainException(
                DomainError.WindowClosed,
                "An unconfirmed session may confirm only until its end time (D-106).");
        }

        if (ActiveGoingCount < MinAttendance)
        {
            throw new DomainException(
                DomainError.Conflict,
                FormattableString.Invariant(
                    $"Confirmation needs {MinAttendance} active Going participants, but there are {ActiveGoingCount} (D-053)."));
        }

        Status = SessionStatus.Confirmed;
        Raise(new SessionConfirmed(Id));
    }

    /// <summary>
    /// D-074/D-106: only below minimum, only with no Going-waitlisted candidate left, and only
    /// before the session starts — a Confirmed in-progress session can never unconfirm.
    /// </summary>
    public void Unconfirm(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status == SessionStatus.Unconfirmed)
        {
            return;
        }

        RequireStatus(SessionStatus.Confirmed, "unconfirm");

        if (clock.GetCurrentInstant() >= StartTime)
        {
            throw new DomainException(
                DomainError.WindowClosed,
                "A Confirmed session that has started cannot unconfirm and will Complete (D-106).");
        }

        if (ActiveGoingCount >= MinAttendance)
        {
            throw new DomainException(
                DomainError.Conflict,
                "Active Going attendance still meets the minimum.");
        }

        if (HasGoingWaitlistedCandidate)
        {
            throw new DomainException(
                DomainError.Conflict,
                "A Going-waitlisted candidate still preserves confirmation (D-074).");
        }

        Status = SessionStatus.Unconfirmed;
        Raise(new SessionUnconfirmed(Id));
    }

    /// <summary>D-056: a Confirmed session completes after its end time; the roster freezes (D-095).</summary>
    public void Complete(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status == SessionStatus.Completed)
        {
            return;
        }

        RequireStatus(SessionStatus.Confirmed, "complete");
        RequireEnded(clock, "complete");

        Status = SessionStatus.Completed;
        LapsePendingJoinRequests(clock);
    }

    /// <summary>D-056/D-164: an unconfirmed past session expires and is never retained in history.</summary>
    public void Expire(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status == SessionStatus.Expired)
        {
            return;
        }

        RequireStatus(SessionStatus.Unconfirmed, "expire");
        RequireEnded(clock, "expire");

        Status = SessionStatus.Expired;
        LapsePendingJoinRequests(clock);
    }

    /// <summary>
    /// D-156/D-157: cancellation is the only "delete", available while Unconfirmed, Confirmed,
    /// in progress or Completed. Because due transitions apply first, a logically-expired session
    /// cannot be canceled and a logically-completed one cancels as Completed. Every guest link is
    /// revoked immediately (D-174) — that notice is the guests' only cancellation channel (D-167).
    /// </summary>
    public void Cancel(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        if (Status == SessionStatus.Canceled)
        {
            return;
        }

        if (Status is not (SessionStatus.Unconfirmed or SessionStatus.Confirmed or SessionStatus.Completed))
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"A {Status} session cannot be canceled (D-156)."));
        }

        Status = SessionStatus.Canceled;

        foreach (GuestLink link in _guestLinks)
        {
            link.Revoke(GuestLinkState.RevokedByCancel);
        }

        LapsePendingJoinRequests(clock);
        Raise(new SessionCanceled(Id, OrganizerId));
    }

    // ---- §5.6 roster commands -----------------------------------------------------------------

    /// <summary>
    /// D-020: the organizer adds registered invitees after creation, before the session starts
    /// (§12-8 — a post-start invitee could never respond, D-081). Pending consumes no capacity;
    /// re-inviting a Declined row flips it back to Pending and re-extends visibility. A
    /// <c>Removed</c> row can never be invited over, and <paramref name="isBlocked"/> is the hook the
    /// visibility stage passes in — the block query lives there, not here.
    /// </summary>
    public Participation Invite(UserId invitee, IClock clock, Func<UserId, bool>? isBlocked = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "invite");
        RequireNotStarted(now, "Invitations close at the session start (§12-8, D-081).");

        if (invitee == OrganizerId)
        {
            throw new DomainException(DomainError.Conflict, "The organizer is already on the roster.");
        }

        if (isBlocked?.Invoke(invitee) == true)
        {
            throw new DomainException(DomainError.Blocked, "A blocked counterpart cannot be invited (D-065/D-077).");
        }

        ParticipantRef participant = ParticipantRef.ForUser(invitee);
        Participation? existing = Find(participant);

        if (existing is null)
        {
            Participation created = Participation.Invited(Id, participant, now);
            _participations.Add(created);
            Raise(new SessionInvitationSent(Id, invitee));
            ReevaluateConfirmation(clock);
            return created;
        }

        if (existing.IsRemoved)
        {
            throw new DomainException(
                DomainError.RemovalIsPermanent,
                "A removed participant is never re-invited over (§5.2).");
        }

        if (existing.Status != ParticipationStatus.Declined)
        {
            throw new DomainException(DomainError.Duplicate, "That user is already on the roster.");
        }

        existing.ReInvite();
        Raise(new SessionInvitationSent(Id, invitee));
        ReevaluateConfirmation(clock);
        return existing;
    }

    /// <summary>
    /// D-084/D-089/D-091/D-106: a Pending or Declined row becoming an active consumer is
    /// capacity-checked and waitlists at maximum without displacing active Maybes; responses close
    /// at the session start (D-081). A Going↔Maybe flip — active <em>and</em> waitlisted alike —
    /// stays open while the session is in progress and never re-tests capacity, but can never
    /// unconfirm a started Confirmed session.
    /// </summary>
    public void Rsvp(ParticipantRef participant, ParticipationStatus response, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (response is not (ParticipationStatus.Going or ParticipationStatus.Maybe))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                "An RSVP is Going or Maybe; declining goes through DeclineInvitation or Leave.");
        }

        ApplyDueTransitions(clock);
        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "RSVP");

        Participation row = RequireParticipation(participant);
        int freeBefore = FreeSlots;

        if (row.Status is ParticipationStatus.Going or ParticipationStatus.Maybe)
        {
            row.SetResponse(response, row.IsWaitlisted, now);
        }
        else
        {
            RequireNotStarted(now, "Invitation responses close at the session start (D-081/D-089).");

            bool waitlisted = !HasFreeSlot;
            row.SetResponse(response, waitlisted, now);

            if (waitlisted)
            {
                Raise(new SessionWaitlistAdded(Id, participant));
            }
        }

        ReevaluateConfirmation(clock);
        RaiseVacancyIfFreed(freeBefore);
    }

    /// <summary>D-081: declining an invitation is a response and closes at the session start.</summary>
    public void DeclineInvitation(ParticipantRef participant, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "decline");

        Participation row = RequireParticipation(participant);

        if (row.Status != ParticipationStatus.Pending)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                "Only a Pending invitation is declined; a Going or Maybe participant leaves (D-055).");
        }

        RequireNotStarted(now, "Invitation responses close at the session start (D-081/D-089).");
        row.SetResponse(ParticipationStatus.Declined, isWaitlisted: false, now);
        ReevaluateConfirmation(clock);
    }

    /// <summary>
    /// D-055: an active or waitlisted Going/Maybe participant leaves, becoming Declined — before the
    /// start only (§12-3; in-progress changes are restricted to Going↔Maybe by D-089). Post-start the
    /// only waitlist exit is <see cref="RejectFromWaitlist"/>.
    /// </summary>
    public void Leave(ParticipantRef participant, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "leave");

        Participation row = RequireParticipation(participant);

        if (row.Status is not (ParticipationStatus.Going or ParticipationStatus.Maybe))
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                "Only a Going or Maybe participant can leave (D-055).");
        }

        RequireNotStarted(now, "Leaving closes at the session start (§12-3).");

        int freeBefore = FreeSlots;
        row.SetResponse(ParticipationStatus.Declined, isWaitlisted: false, now);
        ReevaluateConfirmation(clock);
        RaiseVacancyIfFreed(freeBefore);
    }

    /// <summary>
    /// The organizer drops a waitlisted entry — spec §8.4's "rejected from a waitlist". The row
    /// becomes <c>Removed</c>, so it is never revived by a later reschedule (§12-5), and the
    /// notification is an explicit target because the row just lost its access (§10).
    /// </summary>
    public void RejectFromWaitlist(ParticipantRef participant, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "reject from the waitlist");

        Participation row = RequireParticipation(participant);
        RequireWaitlisted(row);

        row.Remove();
        Raise(new SessionWaitlistRejected(Id, participant));
        ReevaluateConfirmation(clock);
    }

    /// <summary>
    /// The organizer picks whom to promote (spec §7.1-9): the slot fills immediately and the
    /// selected Going/Maybe is preserved. A freed slot is never reserved — a faster RSVP may take it
    /// (§12-4) — so promotion is capacity-guarded under the same row lock as every RSVP.
    /// </summary>
    public void PromoteFromWaitlist(ParticipantRef participant, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "promote");

        Participation row = RequireParticipation(participant);
        RequireWaitlisted(row);

        if (!HasFreeSlot)
        {
            throw new DomainException(
                DomainError.CapacityExceeded,
                FormattableString.Invariant($"The session is full at {MaxCapacity} active participants."));
        }

        row.Activate();
        Raise(new SessionWaitlistPromoted(Id, participant));
        ReevaluateConfirmation(clock);
    }

    // ---- §5.6 edits ---------------------------------------------------------------------------

    /// <summary>
    /// D-063/D-130/D-168: before the start only; the new start is re-validated against the D-130
    /// window; confirmation clears and every Pending/Going/Maybe/waitlisted <em>and Declined</em>
    /// row — the organizer's own included (§12-5) — returns to Pending with its waitlist flag
    /// cleared. <c>Removed</c> rows never reset: rescheduling must not resurrect a blocked user into
    /// the blocker's roster. Reschedule recipients are computed after the reset, so revived invitees
    /// are told.
    /// </summary>
    public void ChangeStartTime(Instant startTime, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireNotTerminal("change the start time");
        RequireNotStarted(now, "The start time cannot be edited after the session starts (D-168).");
        SessionStartWindow.Require(startTime, now);

        StartTime = startTime;

        foreach (Participation row in _participations)
        {
            if (!row.IsRemoved)
            {
                row.ResetToPending();
            }
        }

        Status = SessionStatus.Unconfirmed;
        Raise(new SessionRescheduled(Id));
        ReevaluateConfirmation(clock);
    }

    /// <summary>
    /// D-122/D-141/D-168: before the start only, never above the maximum, and confirmation is
    /// recalculated immediately — a raise above the current Going count still enjoys the D-074
    /// Going-waitlist grace.
    /// </summary>
    public void ChangeMinAttendance(int minAttendance, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireNotTerminal("change the minimum attendance");
        RequireNotStarted(now, "Minimum attendance cannot be edited after the session starts (D-168).");

        if (minAttendance < 1)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Minimum attendance is an integer of at least 1, but was {minAttendance} (D-141)."));
        }

        if (MaxCapacity is { } max && minAttendance > max)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"Minimum attendance {minAttendance} cannot exceed the maximum capacity {max} (D-141)."));
        }

        MinAttendance = minAttendance;
        ReevaluateConfirmation(clock);
    }

    /// <summary>
    /// D-121/D-141: a reduction below active Going + Maybe occupancy or below the minimum is
    /// forbidden, and raising never auto-promotes. Clearing to unlimited is rejected while the
    /// waitlist is non-empty (§12-1) — D-091 forbids a waitlist without a maximum and spec §8.1
    /// keeps promotion manual, so the organizer disposes of the waitlist first.
    /// </summary>
    public void ChangeMaxCapacity(int? maxCapacity, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "change the maximum capacity");

        int freeBefore = FreeSlots;

        if (maxCapacity is { } max)
        {
            if (max < MinAttendance)
            {
                throw new DomainException(
                    DomainError.ValidationFailed,
                    FormattableString.Invariant(
                        $"Maximum capacity {max} cannot be below the minimum attendance {MinAttendance} (D-141)."));
            }

            if (max < ActiveOccupancy)
            {
                throw new DomainException(
                    DomainError.CapacityExceeded,
                    FormattableString.Invariant(
                        $"Maximum capacity cannot drop below the current occupancy of {ActiveOccupancy} (D-121)."));
            }
        }
        else if (WaitlistCount > 0)
        {
            throw new DomainException(
                DomainError.Conflict,
                FormattableString.Invariant(
                    $"Promote or reject the {WaitlistCount} waitlisted participant(s) before clearing the maximum (D-091, §12-1)."));
        }

        MaxCapacity = maxCapacity;
        RaiseVacancyIfFreed(freeBefore);
        ReevaluateConfirmation(clock);
    }

    /// <summary>D-122: a rescheduling notification without resetting RSVPs.</summary>
    public void ChangeDuration(Duration15 duration, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "change the duration");

        if (duration.TotalMinutes < Duration15.MinimumMinutes)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A session lasts {Duration15.MinimumMinutes} minutes to {Duration15.MaximumMinutes} minutes (D-142)."));
        }

        Duration = duration;
        Raise(new SessionRescheduled(Id));
        ReevaluateConfirmation(clock);

        // Shortening the duration can put the derived end time in the past; the shared transitions decide.
        ApplyDueTransitions(clock);
    }

    /// <summary>D-123: silent.</summary>
    public void ChangeTitle(SessionTitle title, IClock clock)
    {
        RequireEditable(clock, "change the title");

        if (title.Value.Length < SessionTitle.MinLength)
        {
            throw new DomainException(DomainError.ValidationFailed, "A session requires a title (D-057).");
        }

        Title = title;
    }

    /// <summary>D-040/D-058/D-176, D-123: silent.</summary>
    public void ChangeGame(Platform platform, SessionGame? game, IClock clock)
    {
        RequireEditable(clock, "change the game");

        if (!Enum.IsDefined(platform))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Unknown platform '{platform}'."));
        }

        game?.EnsureAllowedOn(platform);

        Platform = platform;
        Game = game;
    }

    /// <summary>D-123: silent.</summary>
    public void ChangeNotes(SessionNotes notes, IClock clock)
    {
        RequireEditable(clock, "change the notes");
        Notes = notes;
    }

    /// <summary>
    /// D-123/§12-7: silent in both directions. The retained set on a flip to private <em>is</em> §7
    /// rule 1 (Origin = Invited rows, active participants, Pending requests), so no roster surgery
    /// happens here. D-099's invited-friend rule is creation-time only — a guest-only session may be
    /// flipped public and is then discoverable through its organizer (D-085).
    /// </summary>
    public void ChangePrivacy(bool isPrivate, IClock clock)
    {
        RequireEditable(clock, "change the privacy");
        IsPrivate = isPrivate;
    }

    /// <summary>D-082: a per-session override of the account reminder default; registered only.</summary>
    public void SetReminderChoice(UserId user, ReminderOffset? choice, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "set a reminder choice");
        RequireParticipation(ParticipantRef.ForUser(user)).SetReminderChoice(choice);
    }

    // ---- §5.3 join requests -------------------------------------------------------------------

    /// <summary>
    /// D-173: requests stay open until the session's end time, in progress included. A requester
    /// holding a <c>Removed</c> Participation cannot open one — removal is permanent for this
    /// session (§12-24), and that has to be a domain error here rather than a uniqueness crash at
    /// approval time.
    /// </summary>
    public JoinRequest OpenJoinRequest(UserId requester, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "request to join");

        if (requester == OrganizerId)
        {
            throw new DomainException(DomainError.Conflict, "The organizer already has access.");
        }

        ParticipantRef participant = ParticipantRef.ForUser(requester);

        if (Find(participant) is { } existing)
        {
            if (existing.IsRemoved)
            {
                throw new DomainException(
                    DomainError.RemovalIsPermanent,
                    "A removed participant cannot request to join this session again (§5.3, §12-24).");
            }

            throw new DomainException(DomainError.Conflict, "That user is already on the roster.");
        }

        if (_joinRequests.Exists(r => r.RequesterId == requester && r.IsPending))
        {
            throw new DomainException(DomainError.Duplicate, "One open join request per user per session.");
        }

        JoinRequest request = JoinRequest.ForSession(Id, requester, clock);
        _joinRequests.Add(request);
        Raise(new SessionJoinRequestReceived(Id, request.Id, requester));
        return request;
    }

    /// <summary>
    /// D-098: approval carries no attendance choice — the requester becomes Going, or
    /// Going-waitlisted when the session is at capacity (§12-6: nothing reserves a slot for them).
    /// </summary>
    public Participation ApproveJoinRequest(JoinRequestId joinRequestId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "approve a join request");

        JoinRequest request = RequirePendingJoinRequest(joinRequestId);
        ParticipantRef participant = ParticipantRef.ForUser(request.RequesterId);

        Participation? existing = Find(participant);
        if (existing?.IsRemoved == true)
        {
            request.Lapse(clock);
            throw new DomainException(
                DomainError.RemovalIsPermanent,
                "A removed participant cannot be approved onto this session (§5.3).");
        }

        // An existing Going/Maybe row already settled its capacity question; only a Pending, Declined
        // or brand-new row becomes an active consumer here and is therefore capacity-checked (§5.2).
        bool alreadyResponded = existing is not null
            && existing.Status is ParticipationStatus.Going or ParticipationStatus.Maybe;
        bool waitlisted = alreadyResponded ? existing!.IsWaitlisted : !HasFreeSlot;

        Participation row;
        if (existing is null)
        {
            row = Participation.FromJoinRequest(Id, participant, waitlisted, now);
            _participations.Add(row);
        }
        else
        {
            row = existing;
            row.SetResponse(ParticipationStatus.Going, waitlisted, now);
        }

        request.Approve(clock);
        Raise(new SessionJoinRequestAccepted(Id, request.Id, request.RequesterId));

        if (waitlisted)
        {
            Raise(new SessionWaitlistAdded(Id, participant));
        }

        ReevaluateConfirmation(clock);
        return row;
    }

    /// <summary>D-107: rejection notifies the requester, who holds no participation.</summary>
    public void RejectJoinRequest(JoinRequestId joinRequestId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        JoinRequest request = RequirePendingJoinRequest(joinRequestId);
        request.Reject(clock);
        Raise(new SessionJoinRequestRejected(Id, request.Id, request.RequesterId));
    }

    /// <summary>D-107: withdrawal is silent; the application layer checks that the actor is the requester.</summary>
    public void WithdrawJoinRequest(JoinRequestId joinRequestId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        RequirePendingJoinRequest(joinRequestId).Withdraw(clock);
    }

    /// <summary>Block (§4) and deletion (§11) both lapse a user's open request silently.</summary>
    public void LapseJoinRequestsFrom(UserId requester, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        foreach (JoinRequest request in _joinRequests)
        {
            if (request.RequesterId == requester && request.IsPending)
            {
                request.Lapse(clock);
            }
        }
    }

    // ---- §5.4 guests --------------------------------------------------------------------------

    /// <summary>
    /// D-115: a transferable bearer link, unclaimed at birth. The plaintext token is returned once
    /// and never stored — only its digest lives on the aggregate.
    /// </summary>
    public (GuestLink Link, string Token) CreateGuestLink(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        RequireRosterMutable(clock.GetCurrentInstant(), "create a guest link");

        (GuestLink link, string token) = GuestLink.CreateForSession(Id, clock);
        _guestLinks.Add(link);
        return (link, token);
    }

    /// <summary>
    /// D-017: the first visit supplies a display name and a response. Claims close at the session
    /// start like any invitation response (D-081); the link keeps resolving to a read-only view
    /// afterwards. Unclaimed links reserve nothing, so a guest claiming at capacity waitlists like
    /// anyone (D-084, §12-6).
    /// </summary>
    public GuestIdentity ClaimGuestLink(
        GuestLinkId guestLinkId,
        DisplayName displayName,
        ParticipationStatus response,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "claim a guest link");

        if (response is ParticipationStatus.Removed || !Enum.IsDefined(response))
        {
            throw new DomainException(DomainError.ValidationFailed, "A guest claims with Pending, Going, Maybe or Declined.");
        }

        GuestLink link = RequireActiveLink(guestLinkId);

        if (link.IsClaimed)
        {
            throw new DomainException(DomainError.Conflict, "This guest link has already been claimed.");
        }

        RequireNotStarted(now, "Guest claims close at the session start (D-081).");

        GuestIdentity identity = GuestIdentity.ForSession(Id, displayName, clock);
        _guestIdentities.Add(identity);
        link.Claim(identity.Id, now);

        bool waitlisted = response is ParticipationStatus.Going or ParticipationStatus.Maybe && !HasFreeSlot;
        _participations.Add(Participation.FromGuestLink(Id, identity.Participant, response, waitlisted, now));

        if (waitlisted)
        {
            Raise(new SessionWaitlistAdded(Id, identity.Participant));
        }

        ReevaluateConfirmation(clock);
        return identity;
    }

    /// <summary>
    /// D-044/D-126: guests edit through the same link and multi-device edits are last-write-wins —
    /// there is deliberately no inter-device conflict check, only the capacity lock every response
    /// takes.
    /// </summary>
    public void RespondAsGuest(GuestLinkId guestLinkId, ParticipationStatus response, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        ParticipantRef guest = RequireClaimedGuest(guestLinkId);

        switch (response)
        {
            case ParticipationStatus.Going:
            case ParticipationStatus.Maybe:
                Rsvp(guest, response, clock);
                break;

            case ParticipationStatus.Declined:
                Participation row = RequireParticipation(guest);
                if (row.Status == ParticipationStatus.Declined)
                {
                    return;
                }

                if (row.Status == ParticipationStatus.Pending)
                {
                    DeclineInvitation(guest, clock);
                }
                else
                {
                    Leave(guest, clock);
                }

                break;

            default:
                throw new DomainException(
                    DomainError.ValidationFailed,
                    "A guest responds Going, Maybe or Declined (D-017).");
        }
    }

    /// <summary>D-044/D-126: renaming through the same link, last write wins.</summary>
    public void RenameGuest(GuestLinkId guestLinkId, DisplayName displayName, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "rename a guest");

        ParticipantRef guest = RequireClaimedGuest(guestLinkId);
        RequireGuestIdentity(guest.RequireGuestIdentityId()).Rename(displayName);
    }

    /// <summary>
    /// D-044: a roster command — the participation becomes <c>Removed</c>, every link pointing at
    /// that identity becomes <c>RevokedByRemoval</c>, capacity frees and confirmation is
    /// re-evaluated. Guests are never notified (D-167); the revoked-link notice is the channel.
    /// </summary>
    public void RemoveGuest(GuestIdentityId guestIdentityId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "remove a guest");

        GuestIdentity identity = RequireGuestIdentity(guestIdentityId);
        int freeBefore = FreeSlots;

        if (Find(identity.Participant) is { } row && !row.IsRemoved)
        {
            row.Remove();
        }

        foreach (GuestLink link in _guestLinks)
        {
            if (link.GuestIdentityId == guestIdentityId)
            {
                link.Revoke(GuestLinkState.RevokedByRemoval);
            }
        }

        ReevaluateConfirmation(clock);
        RaiseVacancyIfFreed(freeBefore);
    }

    /// <summary>
    /// D-150 "organizer revocation": an <em>unclaimed</em> link is revoked on its own →
    /// <c>RevokedByRemoval</c>, with no roster effect — it held no participation and reserved no
    /// capacity (§5.4). A claimed link goes through <see cref="RemoveGuest"/> instead (D-044).
    /// </summary>
    public void RevokeGuestLink(GuestLinkId guestLinkId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        RequireRosterMutable(clock.GetCurrentInstant(), "revoke a guest link");

        GuestLink link = _guestLinks.Find(l => l.Id == guestLinkId)
            ?? throw new DomainException(DomainError.NotFound, "No such guest link on this session.");

        if (link.IsClaimed)
        {
            throw new DomainException(
                DomainError.Conflict,
                "A claimed link is revoked by removing its guest (D-044).");
        }

        link.Revoke(GuestLinkState.RevokedByRemoval);
    }

    /// <summary>
    /// Not an organizer command — §12-25 gives the organizer no lever against a registered
    /// participant beyond waitlist rejection and blocking. This is what the block executor (§4) and
    /// the deletion executor (§11 step 3) call; both are system actors (§1). Idempotent.
    /// </summary>
    public void RemoveParticipation(ParticipantRef participant, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        Instant now = clock.GetCurrentInstant();
        RequireRosterMutable(now, "remove a participation");

        Participation row = Find(participant)
            ?? throw new DomainException(DomainError.NotFound, "That participant is not on this session's roster.");

        if (row.IsRemoved)
        {
            return;
        }

        int freeBefore = FreeSlots;
        row.Remove();
        ReevaluateConfirmation(clock);
        RaiseVacancyIfFreed(freeBefore);
    }

    // ---- lookups ------------------------------------------------------------------------------

    public Participation? Find(ParticipantRef participant) => _participations.Find(p => p.Matches(participant));

    public GuestLink? FindLinkByToken(string token) => _guestLinks.Find(link => link.MatchesToken(token));

    /// <summary>§5.4: the pure resolution the API renders for a bearer.</summary>
    public GuestLinkView ResolveGuestLink(GuestLinkId guestLinkId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        GuestLink link = _guestLinks.Find(l => l.Id == guestLinkId)
            ?? throw new DomainException(DomainError.NotFound, "No such guest link on this session.");

        return GuestLinkResolution.ForSession(link.State, Status, EndTime, clock.GetCurrentInstant());
    }

    // ---- internals ----------------------------------------------------------------------------

    /// <summary>
    /// §5.5: re-evaluated after any command touching a participation's status or waitlist flag, the
    /// minimum, the maximum or the start time. Confirm and Unconfirm carry the D-053/D-074/D-106
    /// guards; this only decides which of them is due.
    /// </summary>
    private void ReevaluateConfirmation(IClock clock)
    {
        Instant now = clock.GetCurrentInstant();

        if (Status == SessionStatus.Unconfirmed && ActiveGoingCount >= MinAttendance && now < EndTime)
        {
            Confirm(clock);
            return;
        }

        if (Status == SessionStatus.Confirmed
            && ActiveGoingCount < MinAttendance
            && !HasGoingWaitlistedCandidate
            && now < StartTime)
        {
            Unconfirm(clock);
        }
    }

    private void RaiseVacancyIfFreed(int freeSlotsBefore)
    {
        if (MaxCapacity is null)
        {
            return;
        }

        int freeSlotsNow = FreeSlots;

        if (freeSlotsNow > freeSlotsBefore && freeSlotsNow > 0 && WaitlistCount > 0)
        {
            Raise(new SessionWaitlistVacancy(Id));
        }
    }

    private void LapsePendingJoinRequests(IClock clock)
    {
        foreach (JoinRequest request in _joinRequests)
        {
            if (request.IsPending)
            {
                request.Lapse(clock);
            }
        }
    }

    private Participation RequireParticipation(ParticipantRef participant)
    {
        Participation row = Find(participant)
            ?? throw new DomainException(DomainError.NotFound, "That participant is not on this session's roster.");

        if (row.IsRemoved)
        {
            throw new DomainException(
                DomainError.RemovalIsPermanent,
                "That participation was removed; removal is permanent for this session (§5.2).");
        }

        return row;
    }

    private JoinRequest RequirePendingJoinRequest(JoinRequestId joinRequestId)
    {
        JoinRequest request = _joinRequests.Find(r => r.Id == joinRequestId)
            ?? throw new DomainException(DomainError.NotFound, "No such join request on this session.");

        if (!request.IsPending)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"This join request is already {request.Status}."));
        }

        return request;
    }

    private GuestLink RequireActiveLink(GuestLinkId guestLinkId)
    {
        GuestLink link = _guestLinks.Find(l => l.Id == guestLinkId)
            ?? throw new DomainException(DomainError.NotFound, "No such guest link on this session.");

        if (!link.IsActive)
        {
            throw new DomainException(
                DomainError.Forbidden,
                FormattableString.Invariant($"This guest link is {link.State}."));
        }

        return link;
    }

    private ParticipantRef RequireClaimedGuest(GuestLinkId guestLinkId)
    {
        GuestLink link = RequireActiveLink(guestLinkId);

        GuestIdentityId identityId = link.GuestIdentityId
            ?? throw new DomainException(DomainError.Conflict, "This guest link has not been claimed yet.");

        return ParticipantRef.ForGuest(identityId);
    }

    private GuestIdentity RequireGuestIdentity(GuestIdentityId guestIdentityId) =>
        _guestIdentities.Find(g => g.Id == guestIdentityId)
        ?? throw new DomainException(DomainError.NotFound, "No such guest on this session.");

    private static void RequireWaitlisted(Participation row)
    {
        if (!row.IsWaitlisted)
        {
            throw new DomainException(DomainError.Conflict, "That participant is not waitlisted.");
        }
    }

    private void RequireStatus(SessionStatus expected, string action)
    {
        if (Status != expected)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"Only a {expected} session can {action}, but it is {Status}."));
        }
    }

    private void RequireEnded(IClock clock, string action)
    {
        if (clock.GetCurrentInstant() < EndTime)
        {
            throw new DomainException(
                DomainError.WindowClosed,
                FormattableString.Invariant($"A session can {action} only from {EndTime} onward."));
        }
    }

    private void RequireNotTerminal(string action)
    {
        if (IsTerminal)
        {
            throw new DomainException(
                DomainError.TerminalState,
                FormattableString.Invariant($"A {Status} session cannot {action} (§1 terminal-state gate)."));
        }
    }

    private void RequireNotStarted(Instant now, string message)
    {
        if (now >= StartTime)
        {
            throw new DomainException(DomainError.WindowClosed, message);
        }
    }

    private void RequireRosterMutable(Instant now, string action)
    {
        RequireNotTerminal(action);

        if (now >= EndTime)
        {
            throw new DomainException(
                DomainError.WindowClosed,
                FormattableString.Invariant($"This session ended at {EndTime}; it can no longer {action}."));
        }
    }

    private void RequireEditable(IClock clock, string action)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);
        RequireRosterMutable(clock.GetCurrentInstant(), action);
    }
}
