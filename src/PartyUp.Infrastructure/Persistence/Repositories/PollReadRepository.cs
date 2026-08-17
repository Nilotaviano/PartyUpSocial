using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.FriendGroups;
using PartyUp.Application.Availability;
using PartyUp.Application.Polls;
using PartyUp.Application.Sessions;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Users;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The poll surface's read-repository dependency (PHASE6_DESIGN.md §4): bulk display-name
/// resolution mirroring <c>SessionReadRepository.FetchDisplayNamesAsync</c>, plus the two S4c list
/// bands and the calendar poll layer, mirroring <c>SessionReadRepository</c>'s own list/calendar
/// methods over <see cref="PollVisibilitySql"/> instead of <c>SessionVisibilitySql</c>.
/// </summary>
public sealed class PollReadRepository(AppDbContext context) : IPollReadRepository
{
    public async Task<IReadOnlyDictionary<Guid, UserNameplate>> FindNameplatesAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, UserNameplate>();
        }

        List<UserId> ids = [.. userIds.Distinct()];
        var rows = await context.DomainUsers
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Avatar })
            .ToListAsync(cancellationToken);

        Dictionary<Guid, UserNameplate> result = [];
        foreach (var row in rows)
        {
            result[row.Id.Value] = new UserNameplate(row.DisplayName, row.Avatar);
        }

        return result;
    }

    /// <summary>§4.1: organizer or an Active participant, {Polling, Closed} only (D-160/§1).</summary>
    public async Task<SessionPage<PollSummary>> ListInvolvedAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        short polling = (short)PollStatus.Polling;
        short closed = (short)PollStatus.Closed;
        Guid viewer = viewerId.Value;
        string predicate = PollVisibilitySql.Predicate(PollVisibilitySql.AccessKind.DirectAccess);

        List<Guid> ids = cursor is { } after
            ? await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM polls p
                WHERE status IN ({1}, {2})
                  AND (deadline, id) > ({3}, {4})
                  AND {{predicate}}
                ORDER BY deadline, id
                LIMIT {5}
                """, viewer, polling, closed, after.SortKey, after.Id, pageSize))
                .ToListAsync(cancellationToken)
            : await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM polls p
                WHERE status IN ({1}, {2})
                  AND {{predicate}}
                ORDER BY deadline, id
                LIMIT {3}
                """, viewer, polling, closed, pageSize))
                .ToListAsync(cancellationToken);

        List<Poll> polls = await HydratePollsInOrderAsync(ids, cancellationToken);
        return await ToSummaryPageAsync(polls, pageSize, SessionListScope.PollsInvolved, viewerId, cancellationToken);
    }

    /// <summary>
    /// §4.1: non-private, still <c>Polling</c>, rule-2 inherited access only (D-118 entry point) --
    /// the already-attached exclusion set is organizer, an <em>Active</em> participant row, or a
    /// pending join request. Round-1 divergence from <c>ListDiscoverAsync</c>'s own exclusion
    /// (session: any participation row regardless of status): a poll participant permanently
    /// <c>Removed</c> (§12-24) is not "already attached" in the sense rule 1 means it -- rule 1 has
    /// already failed them -- and <c>VisibilityEngine.Resolve</c> would grant them rule-2 access
    /// through a clean extender exactly like any other non-attached viewer, so the SQL exclusion must
    /// not blanket-exclude a Removed row the way it excludes an Active one.
    /// </summary>
    public async Task<SessionPage<PollSummary>> ListDiscoverableAsync(
        UserId viewerId,
        SessionListCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        short polling = (short)PollStatus.Polling;
        short pendingJoinRequest = (short)JoinRequestStatus.Pending;
        Guid viewer = viewerId.Value;
        string predicate = PollVisibilitySql.Predicate(PollVisibilitySql.AccessKind.InheritedOnly);

        List<Guid> ids = cursor is { } after
            ? await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM polls p
                WHERE status = {1}
                  AND p.organizer_id <> {0}
                  AND NOT EXISTS (SELECT 1 FROM poll_participants pp WHERE pp.poll_id = p.id AND pp.user_id = {0} AND pp.status = 0)
                  AND NOT EXISTS (SELECT 1 FROM join_requests jr WHERE jr.poll_id = p.id AND jr.requester_user_id = {0} AND jr.status = {2})
                  AND (deadline, id) > ({3}, {4})
                  AND {{predicate}}
                ORDER BY deadline, id
                LIMIT {5}
                """, viewer, polling, pendingJoinRequest, after.SortKey, after.Id, pageSize))
                .ToListAsync(cancellationToken)
            : await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
                SELECT id FROM polls p
                WHERE status = {1}
                  AND p.organizer_id <> {0}
                  AND NOT EXISTS (SELECT 1 FROM poll_participants pp WHERE pp.poll_id = p.id AND pp.user_id = {0} AND pp.status = 0)
                  AND NOT EXISTS (SELECT 1 FROM join_requests jr WHERE jr.poll_id = p.id AND jr.requester_user_id = {0} AND jr.status = {2})
                  AND {{predicate}}
                ORDER BY deadline, id
                LIMIT {3}
                """, viewer, polling, pendingJoinRequest, pageSize))
                .ToListAsync(cancellationToken);

        List<Poll> polls = await HydratePollsInOrderAsync(ids, cancellationToken);
        return await ToSummaryPageAsync(polls, pageSize, SessionListScope.PollsDiscoverable, viewerId, cancellationToken);
    }

    /// <summary>
    /// PHASE6_DESIGN.md §5 calendar bullet: rule 1 only, {Polling, Closed}, options overlapping
    /// <paramref name="range"/> -- unpaginated like <c>ListCalendarSessionsAsync</c> (A-079).
    /// </summary>
    public async Task<IReadOnlyList<CalendarPollOption>> ListCalendarPollOptionsAsync(
        UserId viewerId,
        TimeRange range,
        CancellationToken cancellationToken)
    {
        short polling = (short)PollStatus.Polling;
        short closed = (short)PollStatus.Closed;
        Guid viewer = viewerId.Value;
        string predicate = PollVisibilitySql.Predicate(PollVisibilitySql.AccessKind.DirectAccess);

        List<Guid> ids = await context.Database.SqlQuery<Guid>(FormattableStringFactory.Create($$"""
            SELECT DISTINCT p.id FROM polls p
            JOIN poll_options po ON po.poll_id = p.id
            WHERE p.status IN ({1}, {2})
              AND po.start_time < {3}
              AND po.start_time + make_interval(mins => p.duration_minutes) > {4}
              AND {{predicate}}
            ORDER BY p.id
            """, viewer, polling, closed, range.End, range.Start))
            .ToListAsync(cancellationToken);

        List<Poll> polls = await HydratePollsInOrderAsync(ids, cancellationToken);

        List<CalendarPollOption> result = [];
        foreach (Poll poll in polls)
        {
            List<CalendarPollOptionInstant> instants = [];
            foreach (PollOption option in poll.Options)
            {
                if (option.StartTime >= range.End || option.StartTime + option.Duration.Value <= range.Start)
                {
                    continue;
                }

                VoteChoice? ownVote = poll.Votes
                    .FirstOrDefault(v => v.OptionId == option.Id && v.UserId == viewerId)
                    ?.Choice;

                instants.Add(new CalendarPollOptionInstant(option.Id, option.StartTime, ownVote));
            }

            if (instants.Count > 0)
            {
                result.Add(new CalendarPollOption(poll.Id, poll.Title, instants));
            }
        }

        return result;
    }

    private async Task<List<Poll>> HydratePollsInOrderAsync(List<Guid> orderedIds, CancellationToken cancellationToken)
    {
        if (orderedIds.Count == 0)
        {
            return [];
        }

        List<PollId> ids = [.. orderedIds.Select(PollId.From)];
        List<Poll> polls = await context.Polls
            .AsNoTracking()
            .Include(poll => poll.Options)
            .Include(poll => poll.Votes)
            .Where(poll => ids.Contains(poll.Id))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, Poll> byId = polls.ToDictionary(poll => poll.Id.Value);
        return [.. orderedIds.Select(id => byId[id])];
    }

    private async Task<SessionPage<PollSummary>> ToSummaryPageAsync(
        List<Poll> polls,
        int pageSize,
        SessionListScope scope,
        UserId viewerId,
        CancellationToken cancellationToken)
    {
        // D-206: one batched query for the whole page, over the union of Groups-level audiences only.
        List<(Guid TargetId, IReadOnlyCollection<FriendGroupId> Audience)> audiences =
            [.. polls
                .Where(poll => poll.Visibility == ContentVisibility.Groups)
                .Select(poll => (poll.Id.Value, poll.AudienceGroupIds))];
        IReadOnlyDictionary<Guid, GroupProvenance> provenance = audiences.Count == 0
            ? new Dictionary<Guid, GroupProvenance>()
            : await GroupProvenanceReader.ResolveAsync(context, audiences, viewerId, cancellationToken);

        List<PollSummary> summaries = [.. polls.Select(poll => new PollSummary(
            poll.Id,
            poll.OrganizerId,
            poll.Title,
            poll.Status,
            poll.Deadline,
            poll.SelectionWindowEndsAt,
            poll.CreatedAt,
            poll.Options.Count,
            poll.Visibility,
            provenance.GetValueOrDefault(poll.Id.Value)))];

        string? nextCursor = summaries.Count == pageSize && summaries.Count > 0
            ? new SessionListCursor(summaries[^1].Deadline, summaries[^1].Id.Value, scope).Encode()
            : null;

        return new SessionPage<PollSummary>(summaries, nextCursor);
    }
}
