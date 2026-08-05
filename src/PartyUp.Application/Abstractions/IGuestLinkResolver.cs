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
}

/// <summary>The minimal fact the A-064 exchange route needs: which link, and which session it names.</summary>
public sealed record ResolvedGuestLink(GuestLinkId GuestLinkId, SessionId SessionId);
