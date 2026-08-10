using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Polls;

/// <summary>
/// The candidate-times poll aggregate (DOMAIN_MODEL §6). Like the session, its <see cref="Status"/>
/// is materialized rather than re-derived: commands apply any <em>due</em> time transition through
/// the shared idempotent functions first and only then evaluate. The clock gates both directions
/// (§1) — a late Close sweep never delays the organizer's selection, and a stored <c>Polling</c>
/// status never extends voting past the deadline.
/// </summary>
/// <remarks>
/// A poll has no edit surface (§12-26): details and deadline are immutable after creation and only
/// options change. Actor authorization (organizer-only, voter-only) belongs to the application
/// layer; the sweeper, block executor and deletion executor call these functions directly (§1).
/// </remarks>
public sealed class Poll : AggregateRoot
{
    /// <summary>D-080: at most twelve distinct candidate date-times.</summary>
    public const int MaximumOptions = 12;

    /// <summary>D-131: creation requires at least two initial options.</summary>
    public const int MinimumInitialOptions = 2;

    private readonly List<PollOption> _options = [];
    private readonly List<PollParticipant> _participants = [];
    private readonly List<PollVote> _votes = [];
    private readonly List<JoinRequest> _joinRequests = [];
    private readonly List<GuestIdentity> _guestIdentities = [];
    private readonly List<GuestLink> _guestLinks = [];

    private Poll()
    {
    }

    private Poll(PollId id, UserId organizerId, PollDetails details, PollDeadlineChoice deadlineChoice, Instant createdAt)
    {
        Id = id;
        OrganizerId = organizerId;
        Title = details.Title;
        Duration = details.Duration;
        Platform = details.Platform;
        Game = details.Game;
        Notes = details.Notes;
        MinAttendance = details.MinAttendance;
        MaxCapacity = details.MaxCapacity;
        IsPrivate = details.IsPrivate;
        DeadlineChoice = deadlineChoice;
        Deadline = deadlineChoice.DeadlineFrom(createdAt);
        Status = PollStatus.Polling;
        CreatedAt = createdAt;
    }

    public PollId Id { get; private set; }

    public UserId OrganizerId { get; private set; }

    public SessionTitle Title { get; private set; }

    public Duration15 Duration { get; private set; }

    public Platform Platform { get; private set; }

    public SessionGame? Game { get; private set; }

    public SessionNotes Notes { get; private set; }

    public int MinAttendance { get; private set; }

    public int? MaxCapacity { get; private set; }

    public bool IsPrivate { get; private set; }

    /// <summary>D-070: 6, 12, 24 or 48 hours; 24 is the D-131 default, applied by the application layer.</summary>
    public PollDeadlineChoice DeadlineChoice { get; private set; }

    /// <summary>Creation instant + the chosen offset; immutable, like every other poll detail (§12-26).</summary>
    public Instant Deadline { get; private set; }

    public PollStatus Status { get; private set; }

    public Instant CreatedAt { get; private set; }

    /// <summary>Set by finalization; with <see cref="CreatedSessionId"/> it records the poll's outcome.</summary>
    public PollOptionId? SelectedOptionId { get; private set; }

    /// <summary>D-160's poll→session success linkage, from the poll's side.</summary>
    public SessionId? CreatedSessionId { get; private set; }

    /// <summary>D-088: the half-open end of the organizer's 48-hour selection window.</summary>
    public Instant SelectionWindowEndsAt => PollSelectionWindow.EndsAt(Deadline);

    public bool IsTerminal =>
        Status is PollStatus.Finalized or PollStatus.Expired or PollStatus.Canceled;

    /// <summary>D-164/§6.1: only a finalized poll leaves anything in history; its session carries it.</summary>
    public bool IsRetainedInHistory => Status == PollStatus.Finalized;

    public PollDetails Details =>
        new(Title, Duration, Platform, Game, Notes, MinAttendance, MaxCapacity, IsPrivate);

    public IReadOnlyList<PollOption> Options => _options;

    public IReadOnlyList<PollParticipant> Participants => _participants;

    public IReadOnlyList<PollVote> Votes => _votes;

    public IReadOnlyList<JoinRequest> JoinRequests => _joinRequests;

    public IReadOnlyList<GuestIdentity> GuestIdentities => _guestIdentities;

    public IReadOnlyList<GuestLink> GuestLinks => _guestLinks;

    public int ActiveParticipantCount => _participants.Count(p => p.IsActive);

    /// <summary>Voting and suggestion are open only while Polling <em>and</em> before the deadline (D-079).</summary>
    public bool IsVotingOpen(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return Status == PollStatus.Polling && clock.GetCurrentInstant() < Deadline;
    }

    /// <summary>
    /// Creation (spec §7.3). At least two distinct candidate instants are required (D-131) and
    /// identical ones merge on the way in (D-080); a non-private poll needs at least one directly
    /// invited registered friend (D-125) — the friendship check itself is application-layer — while
    /// a private poll may be guest-only. The organizer holds a participant row from creation (§6.1).
    /// </summary>
    public static Poll Create(
        UserId organizerId,
        PollDetails details,
        PollDeadlineChoice deadlineChoice,
        IReadOnlyCollection<Instant> optionStartTimes,
        IReadOnlyCollection<UserId> invitees,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(optionStartTimes);
        ArgumentNullException.ThrowIfNull(invitees);
        ArgumentNullException.ThrowIfNull(clock);

        if (!Enum.IsDefined(deadlineChoice))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Unknown poll deadline choice '{deadlineChoice}' (D-070)."));
        }

        if (!details.IsPrivate && invitees.Count == 0)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                "A non-private poll requires at least one directly invited registered friend at creation (D-125).");
        }

        Instant now = clock.GetCurrentInstant();
        Poll poll = new(PollId.New(), organizerId, details, deadlineChoice, now);
        ParticipantRef organizer = ParticipantRef.ForUser(organizerId);

        poll._participants.Add(PollParticipant.Organizer(poll.Id, organizerId, now));

        foreach (Instant startTime in optionStartTimes)
        {
            PollOptionWindow.Require(startTime, now);

            if (poll._options.Exists(o => o.StartTime == startTime))
            {
                continue;
            }

            poll._options.Add(PollOption.Create(poll.Id, startTime, organizer, now));
        }

        if (poll._options.Count < MinimumInitialOptions)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A poll is created with at least {MinimumInitialOptions} distinct candidate times (D-131)."));
        }

        if (poll._options.Count > MaximumOptions)
        {
            throw new DomainException(
                DomainError.LimitExceeded,
                FormattableString.Invariant($"A poll holds at most {MaximumOptions} distinct options (D-080)."));
        }

        foreach (UserId invitee in invitees)
        {
            if (invitee == organizerId)
            {
                throw new DomainException(DomainError.Conflict, "The organizer is already a poll participant.");
            }

            if (poll._participants.Exists(p => p.Matches(ParticipantRef.ForUser(invitee))))
            {
                throw new DomainException(DomainError.Duplicate, "That user has already been invited.");
            }

            poll._participants.Add(PollParticipant.Create(poll.Id, ParticipantRef.ForUser(invitee), now));
            poll.Raise(new PollInvitationSent(poll.Id, invitee));
        }

        return poll;
    }

    // ---- §6.1 state machine -------------------------------------------------------------------

    /// <summary>
    /// Applies whatever the clock has already made due, ahead of evaluating a command (§1).
    /// Idempotent, and the sweeper's own entry point. A poll that sat unswept past both boundaries
    /// closes and then expires in one pass.
    /// </summary>
    public void ApplyDueTransitions(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();

        if (Status == PollStatus.Polling && now >= Deadline)
        {
            Close(clock);
        }

        if (Status == PollStatus.Closed && now >= SelectionWindowEndsAt)
        {
            Expire(clock);
        }
    }

    /// <summary>
    /// D-079: at the deadline voting closes and the poll waits for the organizer — there is no
    /// automatic winner. Pending join requests lapse, because requests are open only while
    /// <c>Polling</c> (§5.3); guest links are untouched (§5.4).
    /// </summary>
    public void Close(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status == PollStatus.Closed)
        {
            return;
        }

        RequireStatus(PollStatus.Polling, "close");

        if (clock.GetCurrentInstant() < Deadline)
        {
            throw new DomainException(
                DomainError.WindowClosed,
                FormattableString.Invariant($"Voting closes at {Deadline}, not before (D-079)."));
        }

        Status = PollStatus.Closed;
        LapsePendingJoinRequests(clock);
        Raise(new PollClosed(Id));
    }

    /// <summary>
    /// D-088: 48 hours after the deadline without a selection. D-140 is what the guard enforces —
    /// a poll never expires early merely because every option has passed. A poll left unswept past
    /// both boundaries closes on the way through.
    /// </summary>
    public void Expire(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status == PollStatus.Expired)
        {
            return;
        }

        if (clock.GetCurrentInstant() < SelectionWindowEndsAt)
        {
            throw new DomainException(
                DomainError.WindowClosed,
                FormattableString.Invariant(
                    $"A poll expires only from {SelectionWindowEndsAt} onward, never early (D-088/D-140)."));
        }

        if (Status == PollStatus.Polling)
        {
            Close(clock);
        }

        RequireStatus(PollStatus.Closed, "expire");
        ExpireCore(clock);
    }

    /// <summary>
    /// §11 step 2 / D-092: the deletion executor expires the organizer's outstanding polls whatever
    /// the clock says, with the same effects and the same D-170 notification.
    /// </summary>
    public void ExpireForOrganizerDeletion(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status == PollStatus.Expired)
        {
            return;
        }

        if (Status is not (PollStatus.Polling or PollStatus.Closed))
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"A {Status} poll cannot be expired by deletion (D-092)."));
        }

        ExpireCore(clock);
    }

    /// <summary>
    /// D-156: the organizer cancels any time before finalization. Because due transitions apply
    /// first, a logically-expired poll can no longer be canceled (§1). Guest links are revoked
    /// immediately — that notice is the guests' only cancellation channel (D-167, §5.4).
    /// </summary>
    public void Cancel(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        if (Status == PollStatus.Canceled)
        {
            return;
        }

        if (Status is not (PollStatus.Polling or PollStatus.Closed))
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"A {Status} poll cannot be canceled (D-156)."));
        }

        Status = PollStatus.Canceled;

        foreach (GuestLink link in _guestLinks)
        {
            link.Revoke(GuestLinkState.RevokedByCancel);
        }

        LapsePendingJoinRequests(clock);
        Raise(new PollCanceled(Id, OrganizerId));
    }

    // ---- §6.2 options -------------------------------------------------------------------------

    /// <summary>
    /// D-033/D-051: any voter suggests, guests included, and the option appears immediately without
    /// organizer approval. Resolution order under the poll row lock (§6.2): an identical instant
    /// <em>merges</em> — the existing option is returned with no error, even at the twelfth option
    /// (D-080) — and only a genuinely new instant meets the cap (D-171). D-185: suggesting also
    /// records the suggester's own Available vote on the resulting option, overwriting any prior
    /// vote there — this applies on the dedup path too, not only to newly created options.
    /// </summary>
    public PollOption SuggestOption(ParticipantRef suggester, Instant startTime, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();
        RequireVotingOpen(now, "suggest an option");
        PollParticipant row = RequireActiveParticipant(suggester);

        if (_options.Find(o => o.StartTime == startTime) is { } duplicate)
        {
            CastVote(suggester, duplicate.Id, VoteChoice.Available, now, row);
            return duplicate;
        }

        PollOptionWindow.Require(startTime, CreatedAt);

        if (_options.Count >= MaximumOptions)
        {
            throw new DomainException(
                DomainError.LimitExceeded,
                FormattableString.Invariant(
                    $"This poll already holds the maximum of {MaximumOptions} distinct options (D-171)."));
        }

        PollOption option = PollOption.Create(Id, startTime, suggester, now);
        _options.Add(option);
        CastVote(suggester, option.Id, VoteChoice.Available, now, row);
        Raise(new PollOptionSuggested(Id, option.Id, suggester));

        return option;
    }

    /// <summary>
    /// D-080: the organizer deletes <em>any</em> option — there is no floor, so a poll can be
    /// emptied and then only canceled or expired (§12-12). Silent, with no voter notification
    /// (D-088). Votes on the option cascade away, but the <see cref="PollParticipant.FirstResponseAt"/>
    /// values they established are never recomputed (D-172).
    /// </summary>
    public void DeleteOption(PollOptionId optionId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        if (Status is not (PollStatus.Polling or PollStatus.Closed))
        {
            throw new DomainException(
                DomainError.TerminalState,
                FormattableString.Invariant($"A {Status} poll's options can no longer be deleted (§1)."));
        }

        PollOption option = _options.Find(o => o.Id == optionId)
            ?? throw new DomainException(DomainError.NotFound, "No such option on this poll.");

        _options.Remove(option);
        _votes.RemoveAll(v => v.OptionId == optionId);
    }

    // ---- §6.3 votes ---------------------------------------------------------------------------

    /// <summary>
    /// D-033/D-079: an Active participant marks an option Available, Maybe or Unavailable, and may
    /// revise until the deadline. The deadline is evaluated against the clock <em>before</em> any
    /// due transition, so a poll whose Close sweep has not run yet still rejects the vote (§1) —
    /// the stored status never extends voting. The first submission stamps
    /// <see cref="PollParticipant.FirstResponseAt"/> once and for all (D-172, §12-13).
    /// </summary>
    public void Vote(ParticipantRef participant, PollOptionId optionId, VoteChoice choice, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!Enum.IsDefined(choice))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Unknown vote choice '{choice}' (D-033)."));
        }

        Instant now = clock.GetCurrentInstant();
        RequireVotingOpen(now, "vote");

        PollParticipant row = RequireActiveParticipant(participant);

        if (!_options.Exists(o => o.Id == optionId))
        {
            throw new DomainException(DomainError.NotFound, "No such option on this poll.");
        }

        CastVote(participant, optionId, choice, now, row);
    }

    /// <summary>
    /// The single place that writes a vote: revise-vs-add and the D-172 first-response stamp, shared
    /// by <see cref="Vote"/> and the D-185 auto-vote in <see cref="SuggestOption"/> so those two
    /// details never drift apart.
    /// </summary>
    private void CastVote(ParticipantRef participant, PollOptionId optionId, VoteChoice choice, Instant now, PollParticipant row)
    {
        if (_votes.Find(v => v.Matches(optionId, participant)) is { } existing)
        {
            existing.Revise(choice, now);
        }
        else
        {
            _votes.Add(PollVote.Create(Id, optionId, participant, choice, now));
        }

        row.RecordFirstResponse(now);
    }

    /// <summary>
    /// Not an organizer command: this is what the block executor (§4) and the deletion executor
    /// (§11 step 3) call — a removed participant's votes are excluded from finalization mapping and
    /// allocation (§6.3). Idempotent.
    /// </summary>
    public void RemoveParticipant(ParticipantRef participant, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);
        RequireNotTerminal("remove a participant");

        PollParticipant row = FindParticipant(participant)
            ?? throw new DomainException(DomainError.NotFound, "That participant is not on this poll.");

        row.Remove();
    }

    // ---- §6.4 finalization --------------------------------------------------------------------

    /// <summary>
    /// The one atomic transition of §6.4. Clock-gated both ways: selection is valid from the
    /// deadline instant onward even if the Close sweep has not run — the due Close is applied
    /// inline — and invalid from <see cref="SelectionWindowEndsAt"/> onward, half-open like every
    /// window (D-088, §1). Votes on the selected option map per D-051 (Available → Going,
    /// Maybe → Maybe, Unavailable → Declined, no vote → Pending) with the organizer Going
    /// regardless (D-155); overflow beyond the maximum is waitlisted in D-119 order; the created
    /// session's status follows D-153; the poll's guests re-parent with their tokens preserved.
    /// </summary>
    public PollFinalizationResult Finalize(PollOptionId optionId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();

        if (now < Deadline)
        {
            throw new DomainException(
                DomainError.WindowClosed,
                FormattableString.Invariant(
                    $"Selection opens at {Deadline}; voting has to close first (D-079, §12-10)."));
        }

        if (now >= SelectionWindowEndsAt)
        {
            throw new DomainException(
                DomainError.WindowClosed,
                FormattableString.Invariant(
                    $"The 48-hour selection window closed at {SelectionWindowEndsAt} (D-088)."));
        }

        ApplyDueTransitions(clock);
        RequireStatus(PollStatus.Closed, "be finalized");

        PollOption option = _options.Find(o => o.Id == optionId)
            ?? throw new DomainException(DomainError.NotFound, "No such option on this poll.");

        (List<FinalizedParticipation> roster, List<ParticipantRef> waitlisted) = AllocateRoster(option);

        int activeGoing = roster.Count(r => !r.IsWaitlisted && r.Status == ParticipationStatus.Going);
        Instant endTime = option.StartTime + Duration.Value;
        bool meetsMinimum = activeGoing >= MinAttendance;

        // D-153: an ended option settles immediately as Completed or Expired; anything else — future
        // or in progress — goes through the normal confirmation evaluation.
        SessionStatus initialStatus = now >= endTime
            ? meetsMinimum ? SessionStatus.Completed : SessionStatus.Expired
            : meetsMinimum ? SessionStatus.Confirmed : SessionStatus.Unconfirmed;

        Session session = Session.FromPollFinalization(
            Id,
            OrganizerId,
            Details.ForStartTime(option.StartTime),
            roster,
            initialStatus,
            clock);

        foreach (GuestIdentity identity in _guestIdentities)
        {
            session.AdoptGuestIdentity(identity);
        }

        foreach (GuestLink link in _guestLinks)
        {
            session.AdoptGuestLink(link);
        }

        _guestIdentities.Clear();
        _guestLinks.Clear();

        LapsePendingJoinRequests(clock);

        Status = PollStatus.Finalized;
        SelectedOptionId = option.Id;
        CreatedSessionId = session.Id;

        Raise(new PollOptionSelected(Id, option.Id, session.Id));

        foreach (ParticipantRef participant in waitlisted)
        {
            Raise(new PollWaitlistAdded(Id, session.Id, participant));
        }

        return new PollFinalizationResult(
            session,
            option.Id,
            option.StartTime,
            waitlisted,
            new MuteCarryOver(Id, session.Id));
    }

    // ---- §5.3 join requests -------------------------------------------------------------------

    /// <summary>
    /// D-118: eligible friends request to join while the poll is <c>Polling</c> and before the
    /// deadline. A requester holding a <c>Removed</c> participant row cannot open one — removal is
    /// permanent for this poll (§5.3, §12-24).
    /// </summary>
    public JoinRequest OpenJoinRequest(UserId requester, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RequireVotingOpen(clock.GetCurrentInstant(), "request to join");

        if (requester == OrganizerId)
        {
            throw new DomainException(DomainError.Conflict, "The organizer already has access.");
        }

        ParticipantRef participant = ParticipantRef.ForUser(requester);

        if (FindParticipant(participant) is { } existing)
        {
            if (existing.IsRemoved)
            {
                throw new DomainException(
                    DomainError.RemovalIsPermanent,
                    "A removed participant cannot request to join this poll again (§5.3, §12-24).");
            }

            throw new DomainException(DomainError.Conflict, "That user is already a poll participant.");
        }

        if (_joinRequests.Exists(r => r.RequesterId == requester && r.IsPending))
        {
            throw new DomainException(DomainError.Duplicate, "One open join request per user per poll.");
        }

        JoinRequest request = JoinRequest.ForPoll(Id, requester, clock);
        _joinRequests.Add(request);
        Raise(new PollJoinRequestReceived(Id, request.Id, requester));

        return request;
    }

    /// <summary>
    /// D-118: approval makes the requester a voter with access to all current options — an Active
    /// participant, carrying no attendance intent (D-098) and no <c>FirstResponseAt</c> until they
    /// actually vote (§12-13).
    /// </summary>
    public PollParticipant ApproveJoinRequest(JoinRequestId joinRequestId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();
        RequireVotingOpen(now, "approve a join request");

        JoinRequest request = RequirePendingJoinRequest(joinRequestId);
        ParticipantRef participant = ParticipantRef.ForUser(request.RequesterId);

        if (FindParticipant(participant) is { } existing)
        {
            if (existing.IsRemoved)
            {
                request.Lapse(clock);
                throw new DomainException(
                    DomainError.RemovalIsPermanent,
                    "A removed participant cannot be approved onto this poll (§5.3).");
            }

            request.Approve(clock);
            Raise(new PollJoinRequestAccepted(Id, request.Id, request.RequesterId));
            return existing;
        }

        PollParticipant row = PollParticipant.Create(Id, participant, now);
        _participants.Add(row);

        request.Approve(clock);
        Raise(new PollJoinRequestAccepted(Id, request.Id, request.RequesterId));

        return row;
    }

    /// <summary>D-107: rejection notifies the requester, who holds no participant row.</summary>
    public void RejectJoinRequest(JoinRequestId joinRequestId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        JoinRequest request = RequirePendingJoinRequest(joinRequestId);
        request.Reject(clock);
        Raise(new PollJoinRequestRejected(Id, request.Id, request.RequesterId));
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
    /// D-115/D-125: an organizer-minted bearer link, unclaimed at birth — a private poll may be
    /// created with these alone. The plaintext token is returned once and never stored.
    /// </summary>
    public (GuestLink Link, string Token) CreateGuestLink(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RequireVotingOpen(clock.GetCurrentInstant(), "create a guest link");

        (GuestLink link, string token) = GuestLink.CreateForPoll(Id, clock);
        _guestLinks.Add(link);

        return (link, token);
    }

    /// <summary>
    /// D-017/D-033: the first visit supplies a display name and becomes a voter. Claiming closes
    /// with voting — a guest who can no longer vote must not become a voter — while the link keeps
    /// resolving to a read-only view through <c>Closed</c> (§5.4).
    /// </summary>
    public GuestIdentity ClaimGuestLink(GuestLinkId guestLinkId, DisplayName displayName, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Instant now = clock.GetCurrentInstant();
        RequireVotingOpen(now, "claim a guest link");

        GuestLink link = RequireActiveLink(guestLinkId);

        if (link.IsClaimed)
        {
            throw new DomainException(DomainError.Conflict, "This guest link has already been claimed.");
        }

        GuestIdentity identity = GuestIdentity.ForPoll(Id, displayName, clock);
        _guestIdentities.Add(identity);
        link.Claim(identity.Id, now);
        _participants.Add(PollParticipant.Create(Id, identity.Participant, now));

        return identity;
    }

    /// <summary>D-044/D-126: renaming through the same link, last write wins.</summary>
    public void RenameGuest(GuestLinkId guestLinkId, DisplayName displayName, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        RequireNotTerminal("rename a guest");

        ParticipantRef guest = RequireClaimedGuest(guestLinkId);
        RequireGuestIdentity(guest.RequireGuestIdentityId()).Rename(displayName);
    }

    /// <summary>
    /// D-044: the participant row becomes <c>Removed</c> and every link pointing at that identity
    /// becomes <c>RevokedByRemoval</c>. Guests are never notified (D-167); the revoked-link notice
    /// is the channel.
    /// </summary>
    public void RemoveGuest(GuestIdentityId guestIdentityId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        RequireNotTerminal("remove a guest");

        GuestIdentity identity = RequireGuestIdentity(guestIdentityId);

        if (FindParticipant(identity.Participant) is { } row)
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
    }

    /// <summary>D-150: an unclaimed link can be revoked on its own, with no roster effect (§5.4).</summary>
    public void RevokeGuestLink(GuestLinkId guestLinkId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        RequireNotTerminal("revoke a guest link");

        GuestLink link = _guestLinks.Find(l => l.Id == guestLinkId)
            ?? throw new DomainException(DomainError.NotFound, "No such guest link on this poll.");

        if (link.IsClaimed)
        {
            throw new DomainException(
                DomainError.Conflict,
                "A claimed link is revoked by removing its guest (D-044).");
        }

        link.Revoke(GuestLinkState.RevokedByRemoval);
    }

    // ---- lookups ------------------------------------------------------------------------------

    public PollParticipant? FindParticipant(ParticipantRef participant) =>
        _participants.Find(p => p.Matches(participant));

    public PollOption? FindOption(PollOptionId optionId) => _options.Find(o => o.Id == optionId);

    public PollVote? FindVote(PollOptionId optionId, ParticipantRef participant) =>
        _votes.Find(v => v.Matches(optionId, participant));

    public IReadOnlyList<PollVote> VotesOf(ParticipantRef participant) =>
        _votes.FindAll(v => v.CastBy(participant));

    /// <summary>
    /// §12-17: a participant whose every current vote is Unavailable is the poll analogue of a
    /// Declined invitee and does not extend inherited visibility; one with no votes yet does.
    /// </summary>
    public bool VotedUnavailableThroughout(ParticipantRef participant)
    {
        IReadOnlyList<PollVote> votes = VotesOf(participant);

        return votes.Count > 0 && votes.All(v => v.Choice == VoteChoice.Unavailable);
    }

    public GuestLink? FindLinkByToken(string token) => _guestLinks.Find(link => link.MatchesToken(token));

    /// <summary>§5.4: the pure resolution the API renders for a bearer.</summary>
    public GuestLinkView ResolveGuestLink(GuestLinkId guestLinkId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ApplyDueTransitions(clock);

        GuestLink link = _guestLinks.Find(l => l.Id == guestLinkId)
            ?? throw new DomainException(DomainError.NotFound, "No such guest link on this poll.");

        return GuestLinkResolution.ForPoll(link.State, Status);
    }

    // ---- internals ----------------------------------------------------------------------------

    /// <summary>
    /// §6.4-3/§6.4-4. Mapping runs over Active participants only, then D-119 fills active capacity
    /// organizer-first, then Available voters, then Maybe voters, each ordered by
    /// (FirstResponseAt, ParticipantRef id) — a deterministic total order under identical timestamps
    /// (§12-14). The organizer is excluded from the ordered lists so an organizer who voted
    /// Available earliest is allocated exactly once.
    /// </summary>
    private (List<FinalizedParticipation> Roster, List<ParticipantRef> Waitlisted) AllocateRoster(PollOption option)
    {
        ParticipantRef organizer = ParticipantRef.ForUser(OrganizerId);
        List<PollParticipant> availableVoters = [];
        List<PollParticipant> maybeVoters = [];
        List<FinalizedParticipation> nonConsumers = [];

        foreach (PollParticipant participant in _participants)
        {
            if (!participant.IsActive || participant.Matches(organizer))
            {
                continue;
            }

            switch (FindVote(option.Id, participant.Participant)?.Choice)
            {
                case VoteChoice.Available:
                    availableVoters.Add(participant);
                    break;

                case VoteChoice.Maybe:
                    maybeVoters.Add(participant);
                    break;

                case VoteChoice.Unavailable:
                    nonConsumers.Add(new FinalizedParticipation(
                        participant.Participant, ParticipationStatus.Declined, IsWaitlisted: false));
                    break;

                default:
                    nonConsumers.Add(new FinalizedParticipation(
                        participant.Participant, ParticipationStatus.Pending, IsWaitlisted: false));
                    break;
            }
        }

        List<FinalizedParticipation> consumers =
        [
            new FinalizedParticipation(organizer, ParticipationStatus.Going, IsWaitlisted: false),
            .. InAllocationOrder(availableVoters)
                .Select(p => new FinalizedParticipation(p.Participant, ParticipationStatus.Going, IsWaitlisted: false)),
            .. InAllocationOrder(maybeVoters)
                .Select(p => new FinalizedParticipation(p.Participant, ParticipationStatus.Maybe, IsWaitlisted: false)),
        ];

        List<FinalizedParticipation> roster = [];
        List<ParticipantRef> waitlisted = [];

        for (int index = 0; index < consumers.Count; index++)
        {
            if (MaxCapacity is { } max && index >= max)
            {
                roster.Add(consumers[index] with { IsWaitlisted = true });
                waitlisted.Add(consumers[index].Participant);
            }
            else
            {
                roster.Add(consumers[index]);
            }
        }

        roster.AddRange(nonConsumers);

        return (roster, waitlisted);
    }

    private static IEnumerable<PollParticipant> InAllocationOrder(IEnumerable<PollParticipant> voters) =>
        voters
            .OrderBy(p => p.FirstResponseAt ?? Instant.MaxValue)
            .ThenBy(p => p.Participant);

    private void ExpireCore(IClock clock)
    {
        Status = PollStatus.Expired;

        foreach (GuestLink link in _guestLinks)
        {
            link.Revoke(GuestLinkState.ExpiredPoll);
        }

        LapsePendingJoinRequests(clock);
        Raise(new PollExpired(Id));
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

    private PollParticipant RequireActiveParticipant(ParticipantRef participant)
    {
        PollParticipant row = FindParticipant(participant)
            ?? throw new DomainException(DomainError.NotFound, "That participant is not on this poll.");

        if (row.IsRemoved)
        {
            throw new DomainException(
                DomainError.RemovalIsPermanent,
                "That participant was removed; removal is permanent for this poll (§6.3).");
        }

        return row;
    }

    private JoinRequest RequirePendingJoinRequest(JoinRequestId joinRequestId)
    {
        JoinRequest request = _joinRequests.Find(r => r.Id == joinRequestId)
            ?? throw new DomainException(DomainError.NotFound, "No such join request on this poll.");

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
            ?? throw new DomainException(DomainError.NotFound, "No such guest link on this poll.");

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
        ?? throw new DomainException(DomainError.NotFound, "No such guest on this poll.");

    private void RequireStatus(PollStatus expected, string action)
    {
        if (Status != expected)
        {
            throw new DomainException(
                IsTerminal ? DomainError.TerminalState : DomainError.InvalidTransition,
                FormattableString.Invariant($"Only a {expected} poll can {action}, but it is {Status}."));
        }
    }

    private void RequireNotTerminal(string action)
    {
        if (IsTerminal)
        {
            throw new DomainException(
                DomainError.TerminalState,
                FormattableString.Invariant($"A {Status} poll cannot {action} (§1 terminal-state gate)."));
        }
    }

    /// <summary>
    /// The clock gate comes first: before the deadline no time-based transition can be due, so
    /// callers need no separate sweep to satisfy §1, and a poll whose Close sweep is merely late
    /// still rejects the command as a closed window rather than as a state error.
    /// </summary>
    private void RequireVotingOpen(Instant now, string action)
    {
        if (now >= Deadline)
        {
            throw new DomainException(
                DomainError.WindowClosed,
                FormattableString.Invariant($"Voting closed at {Deadline}; it is too late to {action} (D-079)."));
        }

        RequireNotTerminal(action);

        if (Status != PollStatus.Polling)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"A {Status} poll can no longer {action} (D-079)."));
        }
    }
}
