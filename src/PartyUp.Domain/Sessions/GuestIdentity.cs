using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Sessions;

/// <summary>
/// The identity a guest link's first visitor creates by supplying a display name (D-017,
/// DOMAIN_MODEL §5.4). Owned by a session xor a poll; poll-owned identities re-parent onto the
/// session that finalization creates (§6.4-6).
/// </summary>
public sealed class GuestIdentity
{
    private GuestIdentity()
    {
    }

    private GuestIdentity(
        GuestIdentityId id,
        SessionId? sessionId,
        PollId? pollId,
        DisplayName displayName,
        Instant createdAt)
    {
        Id = id;
        SessionId = sessionId;
        PollId = pollId;
        DisplayName = displayName;
        CreatedAt = createdAt;
    }

    public GuestIdentityId Id { get; private set; }

    public SessionId? SessionId { get; private set; }

    public PollId? PollId { get; private set; }

    /// <summary>Self-supplied, 1–50 like a registered display name (§1); guests are never tombstoned (D-150).</summary>
    public DisplayName DisplayName { get; private set; }

    public Instant CreatedAt { get; private set; }

    public ParticipantRef Participant => ParticipantRef.ForGuest(Id);

    public static GuestIdentity ForSession(SessionId sessionId, DisplayName displayName, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        RequireDisplayName(displayName);

        return new GuestIdentity(GuestIdentityId.New(), sessionId, null, displayName, clock.GetCurrentInstant());
    }

    public static GuestIdentity ForPoll(PollId pollId, DisplayName displayName, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        RequireDisplayName(displayName);

        return new GuestIdentity(GuestIdentityId.New(), null, pollId, displayName, clock.GetCurrentInstant());
    }

    /// <summary>D-044/D-126: guests edit through the same link, last write wins.</summary>
    public void Rename(DisplayName displayName)
    {
        RequireDisplayName(displayName);
        DisplayName = displayName;
    }

    /// <summary>§6.4-6: finalization re-parents a poll's guests onto the created session.</summary>
    internal void AttachToSession(SessionId sessionId)
    {
        if (PollId is null)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                "Only a poll-owned guest identity can be re-parented onto a session.");
        }

        SessionId = sessionId;
        PollId = null;
    }

    private static void RequireDisplayName(DisplayName displayName)
    {
        if (displayName.Value.Length < Kernel.DisplayName.MinLength)
        {
            throw new DomainException(DomainError.ValidationFailed, "A guest supplies a display name (D-017).");
        }
    }
}
