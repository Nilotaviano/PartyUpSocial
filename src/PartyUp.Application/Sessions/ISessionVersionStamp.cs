using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Sessions;

/// <summary>
/// A-071's opaque <c>version</c> token port. <see cref="Matches"/> takes the aggregate
/// <see cref="EditSessionUseCase.ExecuteAsync"/> already loaded through
/// <see cref="Abstractions.ISessionRepository.LoadForUpdateAsync"/> — the comparison point is the
/// freshly locked row's own concurrency token, never an earlier, unlocked read.
/// </summary>
public interface ISessionVersionStamp
{
    /// <summary>Issues a token for the given session/xmin pair, for responses that carry <c>version</c>.</summary>
    string Issue(SessionId sessionId, uint xmin);

    /// <summary>Fixed-time-compares <paramref name="token"/> against the locked <paramref name="lockedSession"/>'s
    /// own current concurrency token.</summary>
    bool Matches(Session lockedSession, string token);
}
