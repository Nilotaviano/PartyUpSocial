using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Sessions;

/// <summary>
/// The real implementation of the shipped <see cref="IReminderScheduleWriter"/> port (PHASE4_DESIGN.md
/// §5.1, §7.1), replacing the Phase 3 <c>NoSessionReminderScheduleWriter</c> placeholder. Block effects
/// and deletion effects both drain a <see cref="SessionReminderCanceled"/> event when they remove a
/// participant, never a replan — recomputing a still-live due instant is the session command path's
/// job via <see cref="ISessionReminderHandoffWriter"/>, a separate port. Canceling always wins over
/// whatever state the handoff was in, including <c>Delivered</c>: a removed participant's reminder must
/// die with their access (§9), so <c>delivered_at</c> is cleared along with the state flip and the
/// version increments so a stale in-flight worker is harmless. A handoff that was never planned (no row
/// exists) or is already <c>Canceled</c> is a no-op — cancellation is idempotent.
/// </summary>
public sealed class SessionReminderScheduleWriter(AppDbContext context) : IReminderScheduleWriter
{
    public async Task ApplyAsync(
        SessionReminderCanceled cancellation,
        CancellationToken cancellationToken)
    {
        SessionReminderHandoff? handoff = await context.SessionReminderHandoffs
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.SessionId == cancellation.SessionId &&
                    candidate.RecipientUserId == cancellation.UserId,
                cancellationToken);

        if (handoff is null || handoff.State == SessionReminderHandoffState.Canceled)
        {
            return;
        }

        handoff.State = SessionReminderHandoffState.Canceled;
        handoff.DeliveredAt = null;
        handoff.Version++;
    }
}
