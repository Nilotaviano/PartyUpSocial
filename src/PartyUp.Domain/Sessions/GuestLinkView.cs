using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Sessions;

/// <summary>What a bearer sees when they follow a guest link (DOMAIN_MODEL §5.4).</summary>
public enum GuestLinkView
{
    LiveView = 0,
    CanceledNotice = 1,
    RevokedNotice = 2,
    PollExpiredNotice = 3,
    EndedNotice = 4,
}

/// <summary>
/// Link resolution is deliberately <em>not</em> the fail-closed visibility engine (§7): a bearer
/// token is its own proof, and D-167 promises guests can see a cancellation — that notice is their
/// only cancellation channel. None of these outcomes leaks anything the bearer did not already have.
/// A pure function of the stored state plus the target's clock-evaluated condition.
/// </summary>
public static class GuestLinkResolution
{
    /// <summary>
    /// The notice a recorded revocation forces, or null when the link is still <c>Active</c> and the
    /// target's own state decides.
    /// </summary>
    public static GuestLinkView? FromRevocation(GuestLinkState state) => state switch
    {
        GuestLinkState.Active => null,
        GuestLinkState.RevokedByCancel => GuestLinkView.CanceledNotice,
        GuestLinkState.RevokedByRemoval => GuestLinkView.RevokedNotice,
        GuestLinkState.ExpiredPoll => GuestLinkView.PollExpiredNotice,
        GuestLinkState.Upgraded => GuestLinkView.RevokedNotice,
        _ => throw new DomainException(
            DomainError.ValidationFailed,
            FormattableString.Invariant($"Unknown guest link state '{state}'.")),
    };

    /// <summary>
    /// Session-owned links are valid until the session's end time (D-048); a canceled session shows
    /// the cancellation notice (D-167/D-174) even if its link somehow escaped revocation.
    /// </summary>
    public static GuestLinkView ForSession(GuestLinkState state, SessionStatus status, Instant endTime, Instant now)
    {
        if (FromRevocation(state) is { } revoked)
        {
            return revoked;
        }

        if (!Enum.IsDefined(status))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Unknown session status '{status}'."));
        }

        return status switch
        {
            SessionStatus.Canceled => GuestLinkView.CanceledNotice,
            SessionStatus.Completed or SessionStatus.Expired => GuestLinkView.EndedNotice,
            _ when now >= endTime => GuestLinkView.EndedNotice,
            _ => GuestLinkView.LiveView,
        };
    }

    /// <summary>
    /// Poll-owned links stay Active through <c>Polling</c> and <c>Closed</c>; finalization re-parents
    /// them onto the created session, so a Finalized poll resolves through
    /// <see cref="ForSession"/> instead.
    /// </summary>
    public static GuestLinkView ForPoll(GuestLinkState state, PollStatus status)
    {
        if (FromRevocation(state) is { } revoked)
        {
            return revoked;
        }

        return status switch
        {
            PollStatus.Canceled => GuestLinkView.CanceledNotice,
            PollStatus.Expired => GuestLinkView.PollExpiredNotice,
            PollStatus.Polling or PollStatus.Closed => GuestLinkView.LiveView,
            PollStatus.Finalized => throw new DomainException(
                DomainError.InvalidTransition,
                "A finalized poll's guest links have been re-parented onto its session."),
            _ => throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Unknown poll status '{status}'.")),
        };
    }
}
