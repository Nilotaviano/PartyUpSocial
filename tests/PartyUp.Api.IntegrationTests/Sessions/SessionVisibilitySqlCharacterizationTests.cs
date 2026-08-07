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
/// PHASE5_DESIGN.md §11 "Persistence (S2, integration)", A-077 (a)/P5-VIS-01 (a): the four rule-1..4
/// copies previously inlined across <c>ListUpcomingAsync</c>/<c>ListHistoryAsync</c> (cursor and
/// no-cursor branches of each) are now one shared <c>SessionVisibilitySql</c> fragment. This proves
/// the extraction did not change what either surface returns: the same admitted set, in the same
/// order, whether paged one row at a time (cursor branch) or fetched in one page (no-cursor branch).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionVisibilitySqlCharacterizationTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task UpcomingAndHistoryAreUnchangedByTheExtraction_D018_D019()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId viewerId = await _factory.SeedUserAsync(email: "characterization-viewer@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: "characterization-friend@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: "characterization-stranger@example.test");
        UserId strangerTwoId = await _factory.SeedUserAsync(email: "characterization-stranger-two@example.test");
        UserId blockedId = await _factory.SeedUserAsync(email: "characterization-blocked@example.test");
        UserId blockedByOrganizerId = await _factory.SeedUserAsync(email: "characterization-blocked-by-organizer@example.test");

        await LinkFriendsAsync(context, viewerId, friendId);
        await BlockAsync(context, viewerId, blockedId);
        // Rule 4 mirror: the organizer is the blocker, viewer the blocked party — a test whose green
        // survives deleting one arm of SessionVisibilitySql's block predicate would not close this.
        await BlockAsync(context, blockedByOrganizerId, viewerId);

        // Visible: viewer is organizer.
        SessionId organizerSessionId = await CreateSessionAsync(context, viewerId, isPrivate: true, invitees: []);
        // Visible: viewer directly invited and Going.
        SessionId directSessionId = await CreateSessionAsync(context, strangerId, isPrivate: true, invitees: [viewerId], rsvpGoing: viewerId);
        // Visible: inherited via a friend organizer, viewer holds no participation.
        SessionId inheritedSessionId = await CreateSessionAsync(context, friendId, isPrivate: false, invitees: [strangerId]);
        // Not visible: private, viewer is only a friend of the organizer (rule 3).
        SessionId privateInheritedId = await CreateSessionAsync(context, friendId, isPrivate: true, invitees: []);
        // Not visible: organizer is blocked with viewer, even though public (rule 4). Viewer is the blocker.
        SessionId blockedOrganizerId = await CreateSessionAsync(context, blockedId, isPrivate: false, invitees: [strangerId]);
        // Not visible: rule 4 mirror — organizer is the blocker.
        SessionId blockedByOrganizerSessionId = await CreateSessionAsync(context, blockedByOrganizerId, isPrivate: false, invitees: [strangerId]);
        // Not visible: unrelated public session with no friend path at all.
        SessionId unrelatedId = await CreateSessionAsync(context, strangerId, isPrivate: false, invitees: [strangerTwoId]);

        HashSet<Guid> expectedUpcoming = [organizerSessionId.Value, directSessionId.Value, inheritedSessionId.Value];

        SessionPage<SessionSummary> fullPage = await repository.ListUpcomingAsync(viewerId, null, 20, default);
        Assert.Equal(expectedUpcoming, fullPage.Items.Select(item => item.Id.Value).ToHashSet());
        Assert.DoesNotContain(privateInheritedId.Value, fullPage.Items.Select(item => item.Id.Value));
        Assert.DoesNotContain(blockedOrganizerId.Value, fullPage.Items.Select(item => item.Id.Value));
        Assert.DoesNotContain(blockedByOrganizerSessionId.Value, fullPage.Items.Select(item => item.Id.Value));
        Assert.DoesNotContain(unrelatedId.Value, fullPage.Items.Select(item => item.Id.Value));

        // Cursor branch, one row at a time, must reproduce the same set in the same order as the
        // no-cursor branch's single page.
        List<Guid> pagedIds = [];
        SessionListCursor? cursor = null;
        while (true)
        {
            SessionPage<SessionSummary> page = await repository.ListUpcomingAsync(viewerId, cursor, 1, default);
            if (page.Items.Count == 0)
            {
                break;
            }

            pagedIds.Add(page.Items[0].Id.Value);
            cursor = page.NextCursor is { } next ? SessionListCursor.TryDecode(next) : null;
            if (cursor is null)
            {
                break;
            }
        }

        Assert.Equal(fullPage.Items.Select(item => item.Id.Value), pagedIds);

        // History: flip the organizer session to Completed at the database boundary (A-067: the
        // predicate reads the materialized status column only) and confirm it now surfaces there
        // instead, through the same fragment at the same access-kind.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sessions SET status = 2 WHERE id = {organizerSessionId.Value}");

        SessionPage<SessionSummary> historyPage = await repository.ListHistoryAsync(viewerId, null, 20, default);
        Assert.Contains(organizerSessionId.Value, historyPage.Items.Select(item => item.Id.Value));
        Assert.DoesNotContain(directSessionId.Value, historyPage.Items.Select(item => item.Id.Value));

        List<Guid> pagedHistoryIds = [];
        SessionListCursor? historyCursor = null;
        while (true)
        {
            SessionPage<SessionSummary> page = await repository.ListHistoryAsync(viewerId, historyCursor, 1, default);
            if (page.Items.Count == 0)
            {
                break;
            }

            pagedHistoryIds.Add(page.Items[0].Id.Value);
            historyCursor = page.NextCursor is { } next ? SessionListCursor.TryDecode(next) : null;
            if (historyCursor is null)
            {
                break;
            }
        }

        Assert.Equal(historyPage.Items.Select(item => item.Id.Value), pagedHistoryIds);
    }

    private async Task<SessionId> CreateSessionAsync(
        AppDbContext context,
        UserId organizerId,
        bool isPrivate,
        IReadOnlyCollection<UserId> invitees,
        UserId? rsvpGoing = null)
    {
        SessionDetails details = new(
            SessionTitle.Create("Characterization fixture"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: isPrivate);

        Session session = Session.Create(organizerId, details, invitees, _factory.Clock);
        if (rsvpGoing is { } rsvpUserId)
        {
            session.Rsvp(ParticipantRef.ForUser(rsvpUserId), ParticipationStatus.Going, _factory.Clock);
        }

        _ = session.DequeueEvents();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task LinkFriendsAsync(AppDbContext context, UserId a, UserId b)
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

    private async Task BlockAsync(AppDbContext context, UserId blockerId, UserId blockedId)
    {
        User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
        Block block = Block.Create(blockerId, blockedId, blockedUser.Username!.Value, _factory.Clock);
        context.Blocks.Add(block);
        await context.SaveChangesAsync();
    }
}
