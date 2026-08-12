using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// PHASE8_DESIGN.md §2 Q1: the ratified <c>users.last_seen_at</c> touch. Callers decide whether the
/// write floor has elapsed; this port performs the single unconditional column write.
/// </summary>
public interface IUserActivityWriter
{
    Task TouchLastSeenAsync(UserId id, Instant seenAt, CancellationToken cancellationToken);
}
