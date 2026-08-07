using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// PHASE5_DESIGN.md A-080: <c>ISessionReadRepository.ListCalendarSessionsAsync</c> — access-kind
/// <c>any</c> plus the intersection predicate (start before the range end, derived end after the
/// range start), Canceled/Expired excluded, Completed included, no status re-derivation. Also proves
/// A-077 (d)'s calendar-layer corollary: a <c>Removed</c> participation with an unblocked inherited
/// path still renders here with <c>ViewerRelation.Inherited</c>, even though it never reaches Discover
/// (<see cref="DiscoverReadTests"/>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class CalendarReadTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task SessionLayerSpansTheRangeByDerivedEndTimeAndReadsMaterializedStatus_D149_D056_A067()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId viewerId = await _factory.SeedUserAsync(email: "cal-viewer@example.test");
        Instant now = _factory.Clock.GetCurrentInstant();
        TimeRange range = new(now + Duration.FromHours(1), now + Duration.FromHours(5));

        // Starts before the range and ends after it starts (intersects via derived end time, not containment).
        SessionId spanning = await CreateSessionAsync(
            context, viewerId, now + Duration.FromMinutes(30), Duration15.FromMinutes(90));

        // Starts and ends entirely inside the range.
        SessionId inside = await CreateSessionAsync(
            context, viewerId, now + Duration.FromHours(2), Duration15.FromMinutes(30));

        // Starts after the range ends — no intersection.
        SessionId afterRange = await CreateSessionAsync(
            context, viewerId, now + Duration.FromHours(6), Duration15.FromMinutes(30));

        // Canceled: excluded even though it would otherwise intersect.
        SessionId canceled = await CreateSessionAsync(
            context, viewerId, now + Duration.FromHours(2), Duration15.FromMinutes(30));
        await CancelAsync(context, canceled);

        // Completed: included (D-056) — confirm, then complete.
        SessionId completed = await CreateSessionAsync(
            context, viewerId, now + Duration.FromHours(2), Duration15.FromMinutes(30));
        await ConfirmAndCompleteAsync(context, completed);

        IReadOnlyList<SessionSummary> layer = await reads.ListCalendarSessionsAsync(viewerId, range, default);
        HashSet<Guid> present = [.. layer.Select(s => s.Id.Value)];

        Assert.Contains(spanning.Value, present);
        Assert.Contains(inside.Value, present);
        Assert.DoesNotContain(afterRange.Value, present);
        Assert.DoesNotContain(canceled.Value, present);
        Assert.Contains(completed.Value, present);
    }

    [Fact]
    public async Task RemovedParticipationWithAnInheritedPathStillAppearsInTheCalendarLayer_D018()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();
        var sessionReads = scope.ServiceProvider.GetRequiredService<SessionReadUseCases>();

        UserId viewerId = await _factory.SeedUserAsync(email: "cal-removed-viewer@example.test");
        UserId organizerId = await _factory.SeedUserAsync(email: "cal-removed-organizer@example.test");
        UserId extenderId = await _factory.SeedUserAsync(email: "cal-removed-extender@example.test");
        await MakeAcceptedFriendsAsync(context, viewerId, extenderId);

        Instant now = _factory.Clock.GetCurrentInstant();
        SessionId sessionId = await CreateSessionAsync(
            context, organizerId, now + Duration.FromHours(2), Duration15.FromMinutes(60), [viewerId, extenderId]);
        await RsvpGoingAsync(context, sessionId, extenderId);
        await RemoveParticipationAsync(context, sessionId, viewerId);

        TimeRange range = new(now, now + Duration.FromHours(4));
        IReadOnlyList<SessionSummary> layer = await reads.ListCalendarSessionsAsync(viewerId, range, default);
        SessionSummary row = Assert.Single(layer, s => s.Id == sessionId);
        Assert.Equal(ViewerRelation.Inherited, row.ViewerRelation);

        // Still renders full detail (D-114) even though the join-request action is refused elsewhere.
        SessionDetail? detail = await sessionReads.GetSessionDetailAsync(sessionId, viewerId, default);
        Assert.NotNull(detail);
    }

    private async Task<SessionId> CreateSessionAsync(
        AppDbContext context, UserId organizerId, Instant startTime, Duration15 duration, IReadOnlyCollection<UserId>? invitees = null)
    {
        SessionDetails details = new(
            SessionTitle.Create("Calendar fixture"),
            startTime,
            duration,
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: false);

        // D-099: a non-private session needs at least one directly invited registered friend at
        // creation — fixtures that want an empty roster otherwise draw in an unrelated filler invitee.
        List<UserId> effectiveInvitees = [.. invitees ?? []];
        if (effectiveInvitees.Count == 0)
        {
            effectiveInvitees.Add(await _factory.SeedUserAsync(email: $"cal-filler-{Guid.NewGuid():N}@example.test"));
        }

        Session session = Session.Create(organizerId, details, effectiveInvitees, _factory.Clock);
        _ = session.DequeueEvents();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task RsvpGoingAsync(AppDbContext context, SessionId sessionId, UserId userId)
    {
        Session session = await context.Sessions.Include(s => s.Participations).SingleAsync(s => s.Id == sessionId);
        session.Rsvp(ParticipantRef.ForUser(userId), ParticipationStatus.Going, _factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task RemoveParticipationAsync(AppDbContext context, SessionId sessionId, UserId userId)
    {
        Session session = await context.Sessions.Include(s => s.Participations).SingleAsync(s => s.Id == sessionId);
        session.RemoveParticipation(ParticipantRef.ForUser(userId), _factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task CancelAsync(AppDbContext context, SessionId sessionId)
    {
        Session session = await context.Sessions
            .Include(s => s.Participations)
            .Include(s => s.JoinRequests)
            .Include(s => s.GuestLinks)
            .SingleAsync(s => s.Id == sessionId);
        session.Cancel(_factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task ConfirmAndCompleteAsync(AppDbContext context, SessionId sessionId)
    {
        Session session = await context.Sessions.Include(s => s.Participations).SingleAsync(s => s.Id == sessionId);
        session.Confirm(_factory.Clock);
        _factory.Clock.Advance(Duration.FromHours(6));
        session.Complete(_factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task MakeAcceptedFriendsAsync(AppDbContext context, UserId a, UserId b)
    {
        Friendship friendship = Friendship.Request(
            await context.DomainUsers.SingleAsync(u => u.Id == a),
            await context.DomainUsers.SingleAsync(u => u.Id == b),
            _factory.Clock);
        friendship.Accept(b, _factory.Clock);
        _ = friendship.DequeueEvents();
        context.Friendships.Add(friendship);
        await context.SaveChangesAsync();
    }
}
