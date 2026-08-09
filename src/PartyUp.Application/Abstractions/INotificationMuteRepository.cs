using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

// S1 (PHASE7_DESIGN.md §1) needs only the §11 step 5 purge; S4 extends this port with the mute write
// path (Add / RemoveForTarget) per PHASE7_DESIGN.md §4.
public interface INotificationMuteRepository
{
    /// <summary>§11 step 5: every mute row the user holds, session- or poll-targeted alike.</summary>
    Task DeleteForUserAsync(UserId userId, CancellationToken cancellationToken);
}
