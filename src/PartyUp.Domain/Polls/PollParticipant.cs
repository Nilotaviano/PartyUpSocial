using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Polls;

/// <summary>
/// One voter row per (poll, participant) — DOMAIN_MODEL §6.3. There is no Pending state: an invitee
/// is Active from the moment they are invited and simply has no votes yet. <c>Removed</c> is
/// terminal (block D-077, deletion D-092) and excludes the row from finalization mapping and
/// allocation. The organizer holds one from creation and votes like anyone (D-155).
/// </summary>
public sealed class PollParticipant
{
    private PollParticipant()
    {
    }

    private PollParticipant(PollParticipantId id, PollId pollId, ParticipantRef participant, Instant createdAt)
    {
        Id = id;
        PollId = pollId;
        UserId = participant.UserId;
        GuestIdentityId = participant.GuestIdentityId;
        Status = PollParticipantStatus.Active;
        CreatedAt = createdAt;
    }

    public PollParticipantId Id { get; private set; }

    public PollId PollId { get; private set; }

    public UserId? UserId { get; private set; }

    public GuestIdentityId? GuestIdentityId { get; private set; }

    public PollParticipantStatus Status { get; private set; }

    /// <summary>
    /// D-172/§12-13: write-once and set by the <em>first vote submission</em> only — never by
    /// suggesting an option, never by join-request approval, and never recomputed when a revision
    /// lands or the option a vote was cast on is deleted. It is the D-119 allocation's sort key.
    /// </summary>
    public Instant? FirstResponseAt { get; private set; }

    public Instant CreatedAt { get; private set; }

    public ParticipantRef Participant => ParticipantRef.Create(UserId, GuestIdentityId);

    public bool IsRegistered => UserId.HasValue;

    public bool IsGuest => GuestIdentityId.HasValue;

    public bool IsActive => Status == PollParticipantStatus.Active;

    public bool IsRemoved => Status == PollParticipantStatus.Removed;

    public bool Matches(ParticipantRef participant) =>
        UserId == participant.UserId && GuestIdentityId == participant.GuestIdentityId;

    internal static PollParticipant Organizer(PollId pollId, UserId organizerId, Instant now) =>
        new(PollParticipantId.New(), pollId, ParticipantRef.ForUser(organizerId), now);

    internal static PollParticipant Create(PollId pollId, ParticipantRef participant, Instant now) =>
        new(PollParticipantId.New(), pollId, participant, now);

    internal void RecordFirstResponse(Instant now) => FirstResponseAt ??= now;

    internal void Remove() => Status = PollParticipantStatus.Removed;
}
