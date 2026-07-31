using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Polls;

/// <summary>
/// One candidate start time (DOMAIN_MODEL §6.2). <see cref="SuggestedBy"/> is a
/// <see cref="ParticipantRef"/> because guests suggest times too (D-033/D-051). (PollId, StartTime)
/// is logically unique — identical instants merge rather than duplicate (D-080).
/// </summary>
public sealed class PollOption
{
    private PollOption()
    {
    }

    private PollOption(PollOptionId id, PollId pollId, Instant startTime, ParticipantRef suggestedBy, Instant createdAt)
    {
        Id = id;
        PollId = pollId;
        StartTime = startTime;
        SuggestedByUserId = suggestedBy.UserId;
        SuggestedByGuestIdentityId = suggestedBy.GuestIdentityId;
        CreatedAt = createdAt;
    }

    public PollOptionId Id { get; private set; }

    public PollId PollId { get; private set; }

    /// <summary>An absolute instant (spec §8.5); the poll's duration and details apply unchanged (D-154).</summary>
    public Instant StartTime { get; private set; }

    public UserId? SuggestedByUserId { get; private set; }

    public GuestIdentityId? SuggestedByGuestIdentityId { get; private set; }

    public Instant CreatedAt { get; private set; }

    public ParticipantRef SuggestedBy => ParticipantRef.Create(SuggestedByUserId, SuggestedByGuestIdentityId);

    public bool WasSuggestedBy(ParticipantRef participant) =>
        SuggestedByUserId == participant.UserId && SuggestedByGuestIdentityId == participant.GuestIdentityId;

    /// <summary>D-140: an elapsed option stays visible and selectable.</summary>
    public bool HasElapsed(Instant now) => now >= StartTime;

    internal static PollOption Create(PollId pollId, Instant startTime, ParticipantRef suggestedBy, Instant now) =>
        new(PollOptionId.New(), pollId, startTime, suggestedBy, now);
}
