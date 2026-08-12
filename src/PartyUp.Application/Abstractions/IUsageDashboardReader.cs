using NodaTime;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// D-178/D-151: the SQL-backed usage read model. Every measure derives from existing product tables
/// (PHASE8_DESIGN.md §3 S1) — there is no event pipeline, and status is read from the materialized
/// column, never re-derived from timestamps (A-067).
/// </summary>
public interface IUsageDashboardReader
{
    Task<UsageWindowMeasures> ReadWindowAsync(
        Instant since,
        CancellationToken cancellationToken);

    Task<RepeatSchedulingMeasures> ReadRepeatSchedulingAsync(
        Duration within,
        CancellationToken cancellationToken);
}

/// <param name="Registrations">Accounts whose <c>users.created_at</c> falls in the window.</param>
/// <param name="ActiveUsers">Accounts whose <c>users.last_seen_at</c> falls in the window.</param>
/// <param name="SessionsCreated">Every session row created in the window, poll-derived included.</param>
/// <param name="PollsCreated">Every poll row created in the window.</param>
/// <param name="ConfirmationDenominator">
/// Fixed sessions with <c>origin_poll_id IS NULL</c> plus every created poll, cancellations included —
/// a finalized poll contributes exactly 1, never 2 (D-160/PRODUCT_SPEC.md:485).
/// </param>
/// <param name="ConfirmationSuccesses">
/// Denominator members whose session status is Confirmed or Completed; a poll succeeds through its
/// <c>created_session_id</c> session only (D-056).
/// </param>
public sealed record UsageWindowMeasures(
    int Registrations,
    int ActiveUsers,
    int SessionsCreated,
    int PollsCreated,
    int ConfirmationDenominator,
    int ConfirmationSuccesses);

/// <param name="Organizers">Organizers with at least one creation on record.</param>
/// <param name="RepeatOrganizers">
/// Those who created again within the window measured from their own first creation.
/// </param>
public sealed record RepeatSchedulingMeasures(int Organizers, int RepeatOrganizers);
