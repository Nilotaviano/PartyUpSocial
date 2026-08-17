using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Polls;

/// <summary>
/// One candidate start time and its own length (DOMAIN_MODEL §6.2, D-211). <see cref="SuggestedBy"/>
/// is a <see cref="ParticipantRef"/> because guests suggest times too (D-033/D-051). (PollId,
/// StartTime) is logically unique — identical instants merge rather than duplicate (D-080), so an
/// option's identity is its start alone and the merge keeps the length already recorded.
/// </summary>
public sealed class PollOption
{
    private PollOption()
    {
    }

    private PollOption(
        PollOptionId id,
        PollId pollId,
        Instant startTime,
        Duration15 duration,
        ParticipantRef suggestedBy,
        Instant createdAt)
    {
        if (duration.TotalMinutes < Duration15.MinimumMinutes)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A poll option lasts {Duration15.MinimumMinutes} minutes to {Duration15.MaximumMinutes} minutes (D-142/D-211)."));
        }

        Id = id;
        PollId = pollId;
        StartTime = startTime;
        Duration = duration;
        SuggestedByUserId = suggestedBy.UserId;
        SuggestedByGuestIdentityId = suggestedBy.GuestIdentityId;
        CreatedAt = createdAt;
    }

    public PollOptionId Id { get; private set; }

    public PollId PollId { get; private set; }

    /// <summary>An absolute instant (spec §8.5); the poll's remaining details apply unchanged (D-154).</summary>
    public Instant StartTime { get; private set; }

    /// <summary>D-211: this option's own length; the winning option's becomes the session's (D-142 bounds).</summary>
    public Duration15 Duration { get; private set; }

    public UserId? SuggestedByUserId { get; private set; }

    public GuestIdentityId? SuggestedByGuestIdentityId { get; private set; }

    public Instant CreatedAt { get; private set; }

    public ParticipantRef SuggestedBy => ParticipantRef.Create(SuggestedByUserId, SuggestedByGuestIdentityId);

    /// <summary>The half-open span the option occupies; its end is derived, never stored (§1).</summary>
    public TimeRange Window => TimeRange.FromDuration(StartTime, Duration.Value);

    public bool WasSuggestedBy(ParticipantRef participant) =>
        SuggestedByUserId == participant.UserId && SuggestedByGuestIdentityId == participant.GuestIdentityId;

    /// <summary>D-140: an elapsed option stays visible and selectable. Start-based, not end-based.</summary>
    public bool HasElapsed(Instant now) => now >= StartTime;

    internal static PollOption Create(
        PollId pollId,
        Instant startTime,
        Duration15 duration,
        ParticipantRef suggestedBy,
        Instant now) =>
        new(PollOptionId.New(), pollId, startTime, duration, suggestedBy, now);
}
