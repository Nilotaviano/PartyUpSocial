using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// PHASE7_DESIGN.md §4/ruling 4: a single <c>EXISTS</c> over <c>session_participations</c>,
/// <c>poll_participants</c> and <c>join_requests</c> — creating or organizing a session/poll and
/// RSVPing both leave a participation row for the caller; requesting to join either leaves a
/// <c>join_requests</c> row keyed on <c>requester_user_id</c>. No cache, no stored flag.
/// </summary>
public sealed class PushPromptEligibilityRepository(AppDbContext context) : IPushPromptEligibilityRepository
{
    public async Task<bool> IsEligibleAsync(UserId userId, CancellationToken cancellationToken)
    {
        Guid user = userId.Value;
        // EF Core's SqlQuery<T> scalar overload requires the projected column to be named "Value"
        // (case-sensitive) -- Postgres's own EXISTS(...) column is named "exists", which the missing
        // alias below left unmatched (42703, never exercised until ticket-P7-S4-notes N3's first
        // real HTTP probe of GetMyProfile).
        return await context.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM session_participations WHERE user_id = {user}
                UNION ALL
                SELECT 1 FROM poll_participants WHERE user_id = {user}
                UNION ALL
                SELECT 1 FROM join_requests WHERE requester_user_id = {user}
            ) AS "Value"
            """).SingleAsync(cancellationToken);
    }
}
