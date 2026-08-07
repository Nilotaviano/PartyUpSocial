using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// PHASE5_DESIGN.md §5.1, A-077 (e), P5-VIS-01 (d): the live prod defect this slice closes. Rule 4 of
/// the visibility predicate tests only the (viewer, organizer) pair (DOMAIN_MODEL.md:186), so a
/// third-party session's roster can legitimately still carry two participants who have blocked each
/// other. <see cref="RosterEntry.MaskedForViewer"/> (set only by <c>SessionReadUseCases</c>) is the
/// signal that closes it, kept distinct from the D-092 tombstone null-name rule
/// (DOMAIN_MODEL.md:189).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionRosterMaskingTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task BlockedPairParticipantsAreMaskedInBothDirectionsOnAThirdPartySession_D076()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SessionReadUseCases reads = scope.ServiceProvider.GetRequiredService<SessionReadUseCases>();

        UserId organizerId = await _factory.SeedUserAsync(email: "mask-organizer@example.test");
        UserId vId = await _factory.SeedUserAsync(email: "mask-v@example.test");
        UserId uId = await _factory.SeedUserAsync(email: "mask-u@example.test");

        SessionId sessionId = await CreateSessionAsync(context, organizerId, [vId, uId]);
        await RsvpGoingAsync(context, sessionId, vId);
        await RsvpGoingAsync(context, sessionId, uId);
        await BlockAsync(context, blockerId: vId, blockedId: uId);

        // V's detail read: U carries MaskedForViewer == true and a null display name.
        SessionDetail? vView = await reads.GetSessionDetailAsync(sessionId, vId, default);
        Assert.NotNull(vView);
        RosterEntry uEntryFromV = Assert.Single(vView!.Roster, entry => entry.Participant.UserId == uId);
        Assert.True(uEntryFromV.MaskedForViewer);
        Assert.Null(uEntryFromV.DisplayName);
        RosterEntry vEntryFromV = Assert.Single(vView.Roster, entry => entry.Participant.UserId == vId);
        Assert.False(vEntryFromV.MaskedForViewer);

        // U's detail read: V carries the identical signal — masking is symmetric, direction-independent.
        SessionDetail? uView = await reads.GetSessionDetailAsync(sessionId, uId, default);
        Assert.NotNull(uView);
        RosterEntry vEntryFromU = Assert.Single(uView!.Roster, entry => entry.Participant.UserId == vId);
        Assert.True(vEntryFromU.MaskedForViewer);
        Assert.Null(vEntryFromU.DisplayName);

        // Occupancy and both viewers' own access are unaffected: the session is still visible to both,
        // and every roster entry (including the masked ones) is still counted.
        Assert.Equal(3, vView.Roster.Count);
        Assert.Equal(3, uView.Roster.Count);

        // A third, unrelated viewer with legitimate access sees neither entry masked.
        UserId strangerId = await _factory.SeedUserAsync(email: "mask-stranger@example.test");
        await MakeAcceptedFriendsAsync(context, strangerId, organizerId);
        SessionDetail? strangerView = await reads.GetSessionDetailAsync(sessionId, strangerId, default);
        Assert.NotNull(strangerView);
        Assert.DoesNotContain(strangerView!.Roster, entry => entry.MaskedForViewer);
    }

    [Fact]
    public async Task TombstonedParticipantsAreMaskedForEveryViewer_D092()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SessionReadUseCases reads = scope.ServiceProvider.GetRequiredService<SessionReadUseCases>();

        UserId organizerId = await _factory.SeedUserAsync(email: "tomb-organizer@example.test");
        UserId tombstonedId = await _factory.SeedUserAsync(email: "tomb-participant@example.test");
        UserId viewerId = await _factory.SeedUserAsync(email: "tomb-viewer@example.test");

        SessionId sessionId = await CreateSessionAsync(context, organizerId, [tombstonedId, viewerId]);
        await RsvpGoingAsync(context, sessionId, tombstonedId);
        await RsvpGoingAsync(context, sessionId, viewerId);

        User tombstoned = await context.DomainUsers.SingleAsync(u => u.Id == tombstonedId);
        tombstoned.RequestDeletion(_factory.Clock);
        _factory.Clock.Advance(Duration.FromDays(8));
        tombstoned.MarkDeleted(_factory.Clock);
        await context.SaveChangesAsync();

        // Every viewer sees the same distinct signal: a null name, but never MaskedForViewer — the
        // tombstone rule is not the blocked-pair rule.
        SessionDetail? organizerView = await reads.GetSessionDetailAsync(sessionId, organizerId, default);
        Assert.NotNull(organizerView);
        RosterEntry fromOrganizer = Assert.Single(organizerView!.Roster, entry => entry.Participant.UserId == tombstonedId);
        Assert.Null(fromOrganizer.DisplayName);
        Assert.False(fromOrganizer.MaskedForViewer);

        SessionDetail? peerView = await reads.GetSessionDetailAsync(sessionId, viewerId, default);
        Assert.NotNull(peerView);
        RosterEntry fromPeer = Assert.Single(peerView!.Roster, entry => entry.Participant.UserId == tombstonedId);
        Assert.Null(fromPeer.DisplayName);
        Assert.False(fromPeer.MaskedForViewer);

        // The tombstoned participant's id is still present (an empty account, not a hidden one) —
        // unlike a blocked-pair entry, whose wire mapping (S5) additionally drops it.
        Assert.Equal(tombstonedId, fromPeer.Participant.UserId);
    }

    private async Task<SessionId> CreateSessionAsync(
        AppDbContext context, UserId organizerId, IReadOnlyCollection<UserId> invitees)
    {
        SessionDetails details = new(
            SessionTitle.Create("Masking fixture"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: false);

        Session session = Session.Create(organizerId, details, invitees, _factory.Clock);
        _ = session.DequeueEvents();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task RsvpGoingAsync(AppDbContext context, SessionId sessionId, UserId userId)
    {
        Session session = await context.Sessions
            .Include(s => s.Participations)
            .SingleAsync(s => s.Id == sessionId);
        session.Rsvp(ParticipantRef.ForUser(userId), ParticipationStatus.Going, _factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task BlockAsync(AppDbContext context, UserId blockerId, UserId blockedId)
    {
        User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
        Block block = Block.Create(blockerId, blockedId, blockedUser.Username!.Value, _factory.Clock);
        context.Blocks.Add(block);
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
