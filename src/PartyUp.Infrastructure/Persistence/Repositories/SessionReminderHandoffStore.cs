using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The real <see cref="ISessionReminderHandoffWriter"/> (PHASE4_DESIGN.md §5.1, §7.1) — the planning
/// port a session command calls after any recompute trigger (RSVP, leave, approval,
/// promotion/rejection, <c>SetSessionReminder</c>, start/duration change, cancellation, block/deletion
/// removal). It is not a substitute for <c>IReminderScheduleWriter</c>, which stays
/// <c>SessionReminderScheduleWriter</c>'s (S2a) job for the block/deletion cancellation-only path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivered-vs-reschedule (DOMAIN_MODEL.md §12 interpretation note, PHASE4_DESIGN.md §7.1):</b> when
/// a recomputed due instant for a <c>Delivered</c> handoff is still strictly in the future, the handoff
/// resets to <c>Pending</c>, <c>delivered_at</c> clears, and <c>version</c> increments — the reschedule
/// gets a genuinely new reminder. When the recomputed instant is still in the past, the handoff stays
/// <c>Delivered</c> untouched: a blanket "never recreate" would make every reschedule of an
/// already-reminded session a silent no-op.
/// </para>
/// <para>
/// Every other live state (a fresh row, <c>Pending</c>, <c>Canceled</c> revived by a new eligible plan,
/// or <c>Failed</c> retried by the next trigger) is simply written to the newly computed due instant as
/// <c>Pending</c>, with <c>version</c> incremented — one handoff per (session, recipient), enforced by
/// <c>ux_session_reminder_handoffs_session_recipient</c>.
/// </para>
/// </remarks>
public sealed class SessionReminderHandoffStore(AppDbContext context, IClock clock) : ISessionReminderHandoffWriter
{
    public async Task ReplanAsync(
        Session session,
        UserId recipientUserId,
        CancellationToken cancellationToken,
        ReminderOffset? accountDefault = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        ReminderOffset resolvedDefault;
        if (accountDefault is { } explicitDefault)
        {
            // S4's explicit-offset carry: the caller (SetReminderDefaultUseCase) is replanning
            // against a user mutation not yet flushed in this transaction, so a re-read here would
            // see the stale, pre-mutation default.
            resolvedDefault = explicitDefault;
        }
        else
        {
            ReminderOffset? storedDefault = await context.DomainUsers
                .Where(user => user.Id == recipientUserId)
                .Select(user => (ReminderOffset?)user.ReminderDefault)
                .FirstOrDefaultAsync(cancellationToken);

            resolvedDefault = storedDefault ?? ReminderOffsetExtensions.Default;
        }

        ReminderPlan? plan = ReminderRules.Plan(session, recipientUserId, resolvedDefault, clock);

        SessionReminderHandoff? handoff = await FindAsync(session.Id, recipientUserId, cancellationToken);

        if (plan is null)
        {
            Cancel(handoff);
            return;
        }

        Instant now = clock.GetCurrentInstant();

        if (handoff is null)
        {
            context.SessionReminderHandoffs.Add(new SessionReminderHandoff
            {
                Id = Guid.CreateVersion7(),
                SessionId = session.Id,
                RecipientUserId = recipientUserId,
                DueAt = plan.Value.FireAt,
                State = SessionReminderHandoffState.Pending,
                PlannedAt = now,
                DeliveredAt = null,
                Version = 0,
                Attempts = 0,
                LastAttemptAt = null,
            });
            return;
        }

        if (handoff.State == SessionReminderHandoffState.Delivered)
        {
            if (plan.Value.FireAt > now)
            {
                // §7.1 interpretation note: the reschedule moved the due instant strictly into the
                // future, so this delivered reminder gets a genuinely new due instant.
                handoff.State = SessionReminderHandoffState.Pending;
                handoff.DeliveredAt = null;
                handoff.DueAt = plan.Value.FireAt;
                handoff.PlannedAt = now;
                handoff.Version++;
            }

            // Else: the recomputed instant is still in the past — stays Delivered untouched.
            return;
        }

        handoff.DueAt = plan.Value.FireAt;
        handoff.State = SessionReminderHandoffState.Pending;
        handoff.PlannedAt = now;
        handoff.Version++;
    }

    public async Task CancelAsync(SessionId sessionId, UserId recipientUserId, CancellationToken cancellationToken)
    {
        SessionReminderHandoff? handoff = await FindAsync(sessionId, recipientUserId, cancellationToken);
        Cancel(handoff);
    }

    private Task<SessionReminderHandoff?> FindAsync(
        SessionId sessionId,
        UserId recipientUserId,
        CancellationToken cancellationToken) =>
        context.SessionReminderHandoffs.SingleOrDefaultAsync(
            candidate => candidate.SessionId == sessionId && candidate.RecipientUserId == recipientUserId,
            cancellationToken);

    /// <summary>Cancellation is idempotent: no row, or an already-<c>Canceled</c> row, is a no-op.</summary>
    private static void Cancel(SessionReminderHandoff? handoff)
    {
        if (handoff is null || handoff.State == SessionReminderHandoffState.Canceled)
        {
            return;
        }

        handoff.State = SessionReminderHandoffState.Canceled;
        handoff.DeliveredAt = null;
        handoff.Version++;
    }
}
