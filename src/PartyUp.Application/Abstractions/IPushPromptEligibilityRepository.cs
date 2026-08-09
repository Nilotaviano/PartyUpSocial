using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// PHASE7_DESIGN.md §4/ruling 4's server-derived half of the push-prompt split: whether the caller
/// has completed a D-147 qualifying action — sending a session/poll invitation, creating a session,
/// RSVPing, or requesting to join one — computed per request as a single <c>EXISTS</c> over the
/// caller's own participation, invitation and join-request rows. No cache, no stored flag.
/// </summary>
public interface IPushPromptEligibilityRepository
{
    Task<bool> IsEligibleAsync(UserId userId, CancellationToken cancellationToken);
}
