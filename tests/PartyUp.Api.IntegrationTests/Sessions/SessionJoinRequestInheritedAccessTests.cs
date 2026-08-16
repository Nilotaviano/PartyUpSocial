using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// N1 (Phase 4 S2 fix wave 2): <c>RequestJoinAsync</c>'s post-lock visibility recompute must use the
/// same <see cref="VisibilityEngine"/> rule 2 (DOMAIN_MODEL.md:184) the read side uses -- inherited
/// access via ANY extending active registered participant, not just the organizer. These tests seed a
/// non-private session with a Going participant and prove a friend of that participant (not the
/// organizer) can request to join, that a block anywhere on the relevant path fails closed, and that
/// one blocking extender never poisons a different, clean extender.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionJoinRequestInheritedAccessTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RequesterFriendOfGoingParticipant_SucceedsViaInheritedAccess_D018()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "inherited-organizer-1@example.test");
        UserId extenderId = await _factory.SeedUserAsync(email: "inherited-extender-1@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: "inherited-requester-1@example.test");

        SessionId sessionId = await SeedSessionAsync(organizerId, [extenderId]);
        await SetGoingAsync(sessionId, extenderId);
        await MakeAcceptedFriendsAsync(requesterId, extenderId);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        JoinRequestCommandResult result = await BuildJoinRequestUseCases(scope)
            .RequestJoinAsync(sessionId, requesterId, default);

        Assert.True(result.Succeeded, result.Code);
        Assert.NotNull(result.JoinRequest);
        Assert.Equal(JoinRequestStatus.Pending, result.JoinRequest!.Status);
    }

    [Fact]
    public async Task RequesterBlockedByOrganizer_FailsClosedEvenWithCleanExtender_D065()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "inherited-organizer-2@example.test");
        UserId extenderId = await _factory.SeedUserAsync(email: "inherited-extender-2@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: "inherited-requester-2@example.test");
        await EnsureDistinctUsernamesAsync(organizerId, requesterId);

        SessionId sessionId = await SeedSessionAsync(organizerId, [extenderId]);
        await SetGoingAsync(sessionId, extenderId);
        await MakeAcceptedFriendsAsync(requesterId, extenderId);
        await BlockAsync(organizerId, requesterId);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        JoinRequestCommandResult result = await BuildJoinRequestUseCases(scope)
            .RequestJoinAsync(sessionId, requesterId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("session_not_found", result.Code);
    }

    [Fact]
    public async Task StrangerWithNoRelation_FailsClosed_D065()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "inherited-organizer-3@example.test");
        UserId extenderId = await _factory.SeedUserAsync(email: "inherited-extender-3@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: "inherited-stranger-3@example.test");

        SessionId sessionId = await SeedSessionAsync(organizerId, [extenderId]);
        await SetGoingAsync(sessionId, extenderId);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        JoinRequestCommandResult result = await BuildJoinRequestUseCases(scope)
            .RequestJoinAsync(sessionId, strangerId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("session_not_found", result.Code);
    }

    [Fact]
    public async Task RequesterOnlyExtenderBlocksThem_FailsClosed_D065()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "inherited-organizer-4@example.test");
        UserId extenderId = await _factory.SeedUserAsync(email: "inherited-extender-4@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: "inherited-requester-4@example.test");
        await EnsureDistinctUsernamesAsync(extenderId, requesterId);

        SessionId sessionId = await SeedSessionAsync(organizerId, [extenderId]);
        await SetGoingAsync(sessionId, extenderId);
        await MakeAcceptedFriendsAsync(requesterId, extenderId);
        await BlockAsync(extenderId, requesterId);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        JoinRequestCommandResult result = await BuildJoinRequestUseCases(scope)
            .RequestJoinAsync(sessionId, requesterId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("session_not_found", result.Code);
    }

    [Fact]
    public async Task RequesterHasOneBlockingAndOneCleanExtender_SucceedsViaTheCleanExtender_D018()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "inherited-organizer-5@example.test");
        UserId blockingExtenderId = await _factory.SeedUserAsync(email: "inherited-blocking-extender-5@example.test");
        UserId cleanExtenderId = await _factory.SeedUserAsync(email: "inherited-clean-extender-5@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: "inherited-requester-5@example.test");
        await EnsureDistinctUsernamesAsync(blockingExtenderId, requesterId);

        SessionId sessionId = await SeedSessionAsync(organizerId, [blockingExtenderId, cleanExtenderId]);
        await SetGoingAsync(sessionId, blockingExtenderId);
        await SetGoingAsync(sessionId, cleanExtenderId);
        await MakeAcceptedFriendsAsync(requesterId, blockingExtenderId);
        await MakeAcceptedFriendsAsync(requesterId, cleanExtenderId);
        await BlockAsync(blockingExtenderId, requesterId);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        JoinRequestCommandResult result = await BuildJoinRequestUseCases(scope)
            .RequestJoinAsync(sessionId, requesterId, default);

        Assert.True(result.Succeeded, result.Code);
        Assert.NotNull(result.JoinRequest);
    }

    private async Task<SessionId> SeedSessionAsync(UserId organizerId, IReadOnlyList<UserId> invitees)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Session session = Session.Create(organizerId, SessionDetailsFor(), invitees, _factory.Clock);
        _ = session.DequeueEvents();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task SetGoingAsync(SessionId sessionId, UserId participantId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Session session = await context.Sessions.Include(s => s.Participations)
            .SingleAsync(s => s.Id == sessionId);
        session.Rsvp(ParticipantRef.ForUser(participantId), ParticipationStatus.Going, _factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task MakeAcceptedFriendsAsync(UserId first, UserId second)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Friendship friendship = Friendship.Request(
            await context.DomainUsers.SingleAsync(u => u.Id == first),
            await context.DomainUsers.SingleAsync(u => u.Id == second),
            _factory.Clock);
        friendship.Accept(second, _factory.Clock);
        _ = friendship.DequeueEvents();
        context.Friendships.Add(friendship);
        await context.SaveChangesAsync();
    }

    private async Task BlockAsync(UserId blockerId, UserId blockedId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        string blockedUsername = (await context.DomainUsers.SingleAsync(u => u.Id == blockedId))
            .Username!.Value.Value;
        Block block = Block.Create(blockerId, blockedId, Username.Create(blockedUsername), _factory.Clock);
        _ = block.DequeueEvents();
        context.Blocks.Add(block);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Usernames are the first 14 characters of the (version-7, timestamp-prefixed) user id, so two
    /// users seeded within the same test can collide when created milliseconds apart. Block resolution
    /// goes by username, so every test here that blocks needs the two involved parties distinct.
    /// </summary>
    private async Task EnsureDistinctUsernamesAsync(params UserId[] userIds)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string batchSuffix = Guid.NewGuid().ToString("N");

        for (int i = 0; i < userIds.Length; i++)
        {
            User user = await context.DomainUsers.SingleAsync(u => u.Id == userIds[i]);
            Username username = Username.Create($"inhr{i}{batchSuffix[..13]}");
            user.ChangeUsername(username, _factory.Clock);
            context.UsernameRegistry.Add(UsernameRegistryEntry.ClaimNew(username, user.Id));
        }

        await context.SaveChangesAsync();
    }

    private static SessionJoinRequestUseCases BuildJoinRequestUseCases(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<ISessionRepository>(),
        scope.ServiceProvider.GetRequiredService<ISessionReadRepository>(),
        scope.ServiceProvider.GetRequiredService<ISessionLockRepository>(),
        scope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
        scope.ServiceProvider.GetRequiredService<IBlockRepository>(),
        scope.ServiceProvider.GetRequiredService<IFriendGroupRepository>(),
        scope.ServiceProvider.GetRequiredService<ISessionNotificationMaterializer>(),
        scope.ServiceProvider.GetRequiredService<ISessionReminderHandoffWriter>(),
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
        scope.ServiceProvider.GetRequiredService<IClock>(),
        scope.ServiceProvider.GetRequiredService<InviteEmailService>());

    private SessionDetails SessionDetailsFor() => new(
        SessionTitle.Create("Inherited access session"),
        _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
        Duration15.FromMinutes(90),
        Platform.NotApplicable,
        null,
        SessionNotes.Empty,
        minAttendance: 1,
        maxCapacity: null,
        visibility: (false ? PartyUp.Domain.Kernel.ContentVisibility.Private : PartyUp.Domain.Kernel.ContentVisibility.Public));
}
