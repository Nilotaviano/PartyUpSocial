using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// Resolves a bearer token to the guest link it names, for the A-064 transport-entry exchange.
/// Declared in S1 alongside the other Application ports; implemented by S4's
/// <c>GuestLinkResolver</c> and registered only there (PHASE4_DESIGN.md §5.1).
/// </summary>
public interface IGuestLinkResolver
{
    Task<ResolvedGuestLink?> ResolveAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// The poll-owned analogue (PHASE6_DESIGN.md §4, ticket-P6-S4b): a poll-owned link's token
    /// exchange, resolved alongside <see cref="ResolveAsync"/> rather than folded into it — after
    /// finalization a link's row flips from poll-owned to session-owned with its token preserved
    /// (D-048), so trying <see cref="ResolveAsync"/> first and this second is what makes a
    /// re-exchanged token land on the session branch automatically post-handover.
    /// </summary>
    Task<ResolvedPollGuestLink?> ResolvePollAsync(string token, CancellationToken cancellationToken);
}

/// <summary>The minimal fact the A-064 exchange route needs: which link, and which session it names.</summary>
public sealed record ResolvedGuestLink(GuestLinkId GuestLinkId, SessionId SessionId);

/// <summary>The poll-owned analogue of <see cref="ResolvedGuestLink"/>.</summary>
public sealed record ResolvedPollGuestLink(GuestLinkId GuestLinkId, PollId PollId);
