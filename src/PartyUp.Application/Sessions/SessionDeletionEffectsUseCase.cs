using PartyUp.Application.Deletion;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Sessions;

/// <summary>
/// A thin, named entry point over the S2a <see cref="ExecuteDeletionForUserUseCase"/> — the §6.2
/// pre-lock/reload protocol and its bounded outer retry already live there; this simply gives the
/// Sessions namespace a call the Phase 2 grace-expiry sweep (or a future Sessions-scoped caller) can
/// invoke without importing the Deletion namespace directly.
/// </summary>
public sealed class SessionDeletionEffectsUseCase(ExecuteDeletionForUserUseCase deletion)
{
    public Task<bool> ExecuteAsync(UserId subjectId, CancellationToken cancellationToken) =>
        deletion.ExecuteAsync(subjectId, cancellationToken);
}
