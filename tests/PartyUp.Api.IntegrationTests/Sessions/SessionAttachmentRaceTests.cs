using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Application.Email;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// The mandatory P4-LK-01 race gates (PHASE4_DESIGN.md §6.1, exact semantics): attachment-first (invite
/// or approval commits before a block observes and removes/lapses it — a deterministic sequence, since
/// the block scope's own candidate-discovery query only ever sees committed attachments, so there is no
/// database-level race to construct in that direction) and block-first (block commits first while the
/// competing attachment command is genuinely blocked on the same contended <c>users</c> row — invite's
/// and approval's own <c>ISessionLockRepository.LockUsersAsync</c> use <c>FOR NO KEY UPDATE</c>, which
/// conflicts with the block path's <c>FriendPairLocker FOR UPDATE</c> — then rechecks under its own
/// locks and finds the fresh block, writing nothing).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionAttachmentRaceTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task InviteThenBlock_RemovesAttachmentAndCancelsHandoff_D077()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "attach-invite-organizer@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(email: "attach-invite-invitee@example.test");
        await EnsureDistinctUsernamesAsync(organizerId, inviteeId);

        SessionId sessionId = await SeedAcceptedFriendshipSessionAsync(organizerId, inviteeId);

        await using (AsyncServiceScope inviteScope = _factory.Services.CreateAsyncScope())
        {
            SessionCommandResult result = await BuildInvitationUseCases(inviteScope)
                .InviteRegisteredUsersAsync(sessionId, organizerId, [inviteeId], default);
            Assert.True(result.Succeeded, result.Code);
        }

        // Setup, not the race: the invitee attends, which is what makes the reminder handoff this test
        // proves gets canceled actually exist. Invite alone leaves a Pending row with no eligible plan
        // (ReminderRules.IsEligible requires Going/Maybe) — see the class doc for why this direction is
        // sequential rather than a genuine lock race.
        await using (AsyncServiceScope rsvpScope = _factory.Services.CreateAsyncScope())
        {
            SessionCommandResult result = await BuildParticipationUseCases(rsvpScope)
                .RespondToInvitationAsync(sessionId, inviteeId, ParticipationStatus.Going, default);
            Assert.True(result.Succeeded, result.Code);
        }

        await using (AsyncServiceScope handoffScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = handoffScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(SessionReminderHandoffState.Pending, (await context.SessionReminderHandoffs
                .AsNoTracking()
                .SingleAsync(h => h.SessionId == sessionId && h.RecipientUserId == inviteeId)).State);
        }

        await using (AsyncServiceScope blockScope = _factory.Services.CreateAsyncScope())
        {
            BlockGraphCommandResult result = await BlockAsync(blockScope, organizerId, inviteeId);
            Assert.True(result.Succeeded, result.Code);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True((await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == sessionId && p.UserId == inviteeId)).IsRemoved);
        SessionReminderHandoff? handoff = await verify.SessionReminderHandoffs.AsNoTracking()
            .SingleOrDefaultAsync(h => h.SessionId == sessionId && h.RecipientUserId == inviteeId);
        Assert.True(
            handoff is null || handoff.State == SessionReminderHandoffState.Canceled,
            "No active reminder must survive the block (D-077).");
        Assert.Equal(0, await verify.Friendships.CountAsync());
        Assert.Equal(1, await verify.Blocks.CountAsync());
    }

    [Fact]
    public async Task ApproveThenBlock_RemovesAttachment_D077()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "attach-approve-organizer@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: "attach-approve-requester@example.test");
        await EnsureDistinctUsernamesAsync(organizerId, requesterId);

        SessionId sessionId;
        JoinRequestId joinRequestId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Session session = Session.Create(organizerId, SessionDetailsFor(), [], _factory.Clock);
            JoinRequest request = session.OpenJoinRequest(requesterId, _factory.Clock);
            _ = session.DequeueEvents();
            sessionId = session.Id;
            joinRequestId = request.Id;
            context.Sessions.Add(session);
            await context.SaveChangesAsync();
        }

        await using (AsyncServiceScope approveScope = _factory.Services.CreateAsyncScope())
        {
            JoinRequestCommandResult result = await BuildJoinRequestUseCases(approveScope)
                .ApproveJoinRequestAsync(sessionId, organizerId, joinRequestId, default);
            Assert.True(result.Succeeded, result.Code);
        }

        await using (AsyncServiceScope handoffScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = handoffScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(SessionReminderHandoffState.Pending, (await context.SessionReminderHandoffs
                .AsNoTracking()
                .SingleAsync(h => h.SessionId == sessionId && h.RecipientUserId == requesterId)).State);
        }

        await using (AsyncServiceScope blockScope = _factory.Services.CreateAsyncScope())
        {
            BlockGraphCommandResult result = await BlockAsync(blockScope, organizerId, requesterId);
            Assert.True(result.Succeeded, result.Code);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True((await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == sessionId && p.UserId == requesterId)).IsRemoved);
        SessionReminderHandoff handoff = await verify.SessionReminderHandoffs.AsNoTracking()
            .SingleAsync(h => h.SessionId == sessionId && h.RecipientUserId == requesterId);
        Assert.Equal(SessionReminderHandoffState.Canceled, handoff.State);
        Assert.Null(handoff.DeliveredAt);
    }

    [Fact]
    public async Task BlockThenInvite_WritesNothingAndReturnsHidden_D065()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "block-first-invite-organizer@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(email: "block-first-invite-invitee@example.test");
        await EnsureDistinctUsernamesAsync(organizerId, inviteeId);
        SessionId sessionId = await SeedAcceptedFriendshipSessionAsync(organizerId, inviteeId);

        await using AsyncServiceScope blockerScope = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope inviteScope = _factory.Services.CreateAsyncScope();
        var pairGate = new PairHoldGate();

        Task<BlockGraphCommandResult> block = BlockAsync(
            blockerScope,
            organizerId,
            inviteeId,
            new HoldingFriendPairLocker(
                new FriendPairLocker(blockerScope.ServiceProvider.GetRequiredService<AppDbContext>()),
                pairGate));
        await pairGate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var lockAttemptGate = new AttemptGate();
        Task<SessionCommandResult> invite = new SessionInvitationUseCases(
                inviteScope.ServiceProvider.GetRequiredService<ISessionRepository>(),
                new AttemptingSessionLockRepository(
                    inviteScope.ServiceProvider.GetRequiredService<ISessionLockRepository>(),
                    lockAttemptGate),
                inviteScope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
                inviteScope.ServiceProvider.GetRequiredService<IBlockRepository>(),
                inviteScope.ServiceProvider.GetRequiredService<ISessionNotificationMaterializer>(),
                inviteScope.ServiceProvider.GetRequiredService<ISessionReminderHandoffWriter>(),
                inviteScope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                inviteScope.ServiceProvider.GetRequiredService<IClock>(),
                inviteScope.ServiceProvider.GetRequiredService<InviteEmailService>())
            .InviteRegisteredUsersAsync(sessionId, organizerId, [inviteeId], default);

        await lockAttemptGate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(invite.IsCompleted, "Invite must still be blocked behind the block's held organizer row.");

        pairGate.Release.TrySetResult();

        BlockGraphCommandResult blockResult = await block.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(blockResult.Succeeded);

        SessionCommandResult inviteResult = await invite.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(inviteResult.Succeeded);
        Assert.Equal("user_not_found", inviteResult.Code);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verify.SessionParticipations.CountAsync(p =>
            p.SessionId == sessionId && p.UserId == inviteeId));
        Assert.Equal(0, await verify.SessionReminderHandoffs.CountAsync(h =>
            h.SessionId == sessionId && h.RecipientUserId == inviteeId));
        Assert.Equal(0, await verify.Friendships.CountAsync());
        Assert.Equal(1, await verify.Blocks.CountAsync());
        Assert.Equal(0, await verify.Notifications.CountAsync(n => n.SessionId == sessionId));
    }

    [Fact]
    public async Task BlockThenApprove_WritesNothingAndReturnsHidden_D065()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "block-first-approve-organizer@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: "block-first-approve-requester@example.test");
        await EnsureDistinctUsernamesAsync(organizerId, requesterId);

        SessionId sessionId;
        JoinRequestId joinRequestId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Session session = Session.Create(organizerId, SessionDetailsFor(), [], _factory.Clock);
            JoinRequest request = session.OpenJoinRequest(requesterId, _factory.Clock);
            _ = session.DequeueEvents();
            sessionId = session.Id;
            joinRequestId = request.Id;
            context.Sessions.Add(session);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope blockerScope = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope approveScope = _factory.Services.CreateAsyncScope();
        var pairGate = new PairHoldGate();

        Task<BlockGraphCommandResult> block = BlockAsync(
            blockerScope,
            organizerId,
            requesterId,
            new HoldingFriendPairLocker(
                new FriendPairLocker(blockerScope.ServiceProvider.GetRequiredService<AppDbContext>()),
                pairGate));
        await pairGate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var lockAttemptGate = new AttemptGate();
        Task<JoinRequestCommandResult> approve = new SessionJoinRequestUseCases(
                approveScope.ServiceProvider.GetRequiredService<ISessionRepository>(),
                approveScope.ServiceProvider.GetRequiredService<ISessionReadRepository>(),
                new AttemptingSessionLockRepository(
                    approveScope.ServiceProvider.GetRequiredService<ISessionLockRepository>(),
                    lockAttemptGate),
                approveScope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
                approveScope.ServiceProvider.GetRequiredService<IBlockRepository>(),
                approveScope.ServiceProvider.GetRequiredService<IFriendGroupRepository>(),
                approveScope.ServiceProvider.GetRequiredService<ISessionNotificationMaterializer>(),
                approveScope.ServiceProvider.GetRequiredService<ISessionReminderHandoffWriter>(),
                approveScope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                approveScope.ServiceProvider.GetRequiredService<IClock>(),
                approveScope.ServiceProvider.GetRequiredService<InviteEmailService>())
            .ApproveJoinRequestAsync(sessionId, organizerId, joinRequestId, default);

        await lockAttemptGate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(approve.IsCompleted, "Approve must still be blocked behind the block's held organizer row.");

        pairGate.Release.TrySetResult();

        BlockGraphCommandResult blockResult = await block.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(blockResult.Succeeded);

        // The block already lapsed the pending join request unconditionally (BlockEffects.Apply runs
        // CollectPending + LapseJoinRequestsFrom before any terminal/ended check) and committed before
        // approve's own user-row lock could clear. By the time approve's lock unblocks, it re-locks the
        // organizer/requester block pair under its own P4-LK-01 step 3 and finds the fresh block first
        // — block-first takes priority over "not pending", so approve returns the same
        // session_not_found a missing session or request would (D-065), writing and notifying nothing.
        JoinRequestCommandResult approveResult = await approve.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(approveResult.Succeeded);
        Assert.Equal("session_not_found", approveResult.Code);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verify.SessionParticipations.CountAsync(p =>
            p.SessionId == sessionId && p.UserId == requesterId));
        Assert.Equal(0, await verify.SessionReminderHandoffs.CountAsync(h =>
            h.SessionId == sessionId && h.RecipientUserId == requesterId));
        JoinRequest reloadedRequest = await verify.JoinRequests.AsNoTracking()
            .SingleAsync(r => r.Id == joinRequestId);
        Assert.Equal(JoinRequestStatus.Lapsed, reloadedRequest.Status);
        Assert.Equal(1, await verify.Blocks.CountAsync());
        Assert.Equal(0, await verify.Notifications.CountAsync(n => n.SessionId == sessionId));
    }

    /// <summary>
    /// <see cref="SessionJoinRequestUseCases.RequestJoinAsync"/>'s pre-lock probe (its own step 1, no
    /// locks taken) resolves visibility and passes -- the session is non-private (D-019: a private
    /// session would already hide it from an uninvited stranger, which is a different, non-racing 404)
    /// and the block does not exist yet. The block then commits entirely while the request is genuinely
    /// contended on the block's held organizer row (the same <c>FriendPairLocker FOR UPDATE</c> vs.
    /// <c>ISessionLockRepository.LockUsersAsync FOR NO KEY UPDATE</c> conflict the other block-first
    /// tests use), landing strictly between the pre-lock probe and the parent-session lock. This must
    /// fail if the post-lock <c>VisibilityEngine</c> recompute (SessionJoinRequestUseCases.cs:117-124)
    /// is ever removed -- without it, the freshly locked block would never be observed and the request
    /// would wrongly succeed.
    /// </summary>
    [Fact]
    public async Task BlockThenRequestJoin_WritesNothingAndReturnsHidden_D065()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "block-first-requestjoin-organizer@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: "block-first-requestjoin-requester@example.test");
        UserId invitedFriendId = await _factory.SeedUserAsync(email: "block-first-requestjoin-friend@example.test");
        await EnsureDistinctUsernamesAsync(organizerId, requesterId);
        SessionId sessionId = await SeedPublicSessionAsync(organizerId, invitedFriendId);

        // The requester is an accepted friend of the organizer -- not invited or a participant -- so
        // the non-private session's pre-lock probe grants rule-2 inherited access (DOMAIN_MODEL.md:184)
        // through the organizer directly, without touching the D-099 invited-friend seeded above.
        await using (AsyncServiceScope friendScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = friendScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Friendship friendship = Friendship.Request(
                await context.DomainUsers.SingleAsync(u => u.Id == organizerId),
                await context.DomainUsers.SingleAsync(u => u.Id == requesterId),
                _factory.Clock);
            friendship.Accept(requesterId, _factory.Clock);
            _ = friendship.DequeueEvents();
            context.Friendships.Add(friendship);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope blockerScope = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope requestScope = _factory.Services.CreateAsyncScope();
        var pairGate = new PairHoldGate();

        Task<BlockGraphCommandResult> block = BlockAsync(
            blockerScope,
            organizerId,
            requesterId,
            new HoldingFriendPairLocker(
                new FriendPairLocker(blockerScope.ServiceProvider.GetRequiredService<AppDbContext>()),
                pairGate));
        await pairGate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var lockAttemptGate = new AttemptGate();
        Task<JoinRequestCommandResult> requestJoin = new SessionJoinRequestUseCases(
                requestScope.ServiceProvider.GetRequiredService<ISessionRepository>(),
                requestScope.ServiceProvider.GetRequiredService<ISessionReadRepository>(),
                new AttemptingSessionLockRepository(
                    requestScope.ServiceProvider.GetRequiredService<ISessionLockRepository>(),
                    lockAttemptGate),
                requestScope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
                requestScope.ServiceProvider.GetRequiredService<IBlockRepository>(),
                requestScope.ServiceProvider.GetRequiredService<IFriendGroupRepository>(),
                requestScope.ServiceProvider.GetRequiredService<ISessionNotificationMaterializer>(),
                requestScope.ServiceProvider.GetRequiredService<ISessionReminderHandoffWriter>(),
                requestScope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                requestScope.ServiceProvider.GetRequiredService<IClock>(),
                requestScope.ServiceProvider.GetRequiredService<InviteEmailService>())
            .RequestJoinAsync(sessionId, requesterId, default);

        // The Attempted signal fires only once RequestJoin's step-2 LockUsersAsync call is made, which
        // is strictly after its no-lock step-1 probe already resolved visibility successfully -- so by
        // this point the pre-lock probe has already run and passed with no block on record yet.
        await lockAttemptGate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(
            requestJoin.IsCompleted,
            "RequestJoin must still be blocked behind the block's held organizer row.");

        pairGate.Release.TrySetResult();

        BlockGraphCommandResult blockResult = await block.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(blockResult.Succeeded);

        JoinRequestCommandResult requestResult = await requestJoin.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(requestResult.Succeeded);
        Assert.Equal("session_not_found", requestResult.Code);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verify.JoinRequests.CountAsync(r =>
            r.SessionId == sessionId && r.RequesterId == requesterId));
        Assert.Equal(0, await verify.SessionParticipations.CountAsync(p =>
            p.SessionId == sessionId && p.UserId == requesterId));
        Assert.Equal(0, await verify.SessionReminderHandoffs.CountAsync(h =>
            h.SessionId == sessionId && h.RecipientUserId == requesterId));
        Assert.Equal(1, await verify.Friendships.CountAsync());
        Assert.Equal(1, await verify.Blocks.CountAsync());
        Assert.Equal(0, await verify.Notifications.CountAsync(n => n.SessionId == sessionId));
    }

    private async Task<SessionId> SeedAcceptedFriendshipSessionAsync(UserId organizerId, UserId counterpartId)
    {
        SessionId sessionId;
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Session session = Session.Create(organizerId, SessionDetailsFor(), [], _factory.Clock);
        _ = session.DequeueEvents();
        sessionId = session.Id;
        context.Sessions.Add(session);

        Friendship friendship = Friendship.Request(
            await context.DomainUsers.SingleAsync(u => u.Id == organizerId),
            await context.DomainUsers.SingleAsync(u => u.Id == counterpartId),
            _factory.Clock);
        friendship.Accept(counterpartId, _factory.Clock);
        _ = friendship.DequeueEvents();
        context.Friendships.Add(friendship);

        await context.SaveChangesAsync();
        return sessionId;
    }

    /// <summary>A non-private session, so an uninvited requester's pre-lock visibility probe (D-019)
    /// passes on its own -- distinct from <see cref="SeedAcceptedFriendshipSessionAsync"/>'s private
    /// sessions, which an uninvited friend cannot yet see. D-099 requires a non-private session to have
    /// at least one directly invited registered friend at creation, so <paramref name="invitedFriendId"/>
    /// fills that requirement without being party to the race the test itself constructs.</summary>
    private async Task<SessionId> SeedPublicSessionAsync(UserId organizerId, UserId invitedFriendId)
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Friendship friendship = Friendship.Request(
            await context.DomainUsers.SingleAsync(u => u.Id == organizerId),
            await context.DomainUsers.SingleAsync(u => u.Id == invitedFriendId),
            _factory.Clock);
        friendship.Accept(invitedFriendId, _factory.Clock);
        _ = friendship.DequeueEvents();
        context.Friendships.Add(friendship);

        SessionDetails details = new(
            SessionTitle.Create("Attachment race session (public)"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
            Duration15.FromMinutes(90),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: (false ? PartyUp.Domain.Kernel.ContentVisibility.Private : PartyUp.Domain.Kernel.ContentVisibility.Public));
        Session session = Session.Create(organizerId, details, [invitedFriendId], _factory.Clock);
        _ = session.DequeueEvents();
        SessionId sessionId = session.Id;
        context.Sessions.Add(session);

        await context.SaveChangesAsync();
        return sessionId;
    }

    private static SessionInvitationUseCases BuildInvitationUseCases(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<ISessionRepository>(),
        scope.ServiceProvider.GetRequiredService<ISessionLockRepository>(),
        scope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
        scope.ServiceProvider.GetRequiredService<IBlockRepository>(),
        scope.ServiceProvider.GetRequiredService<ISessionNotificationMaterializer>(),
        scope.ServiceProvider.GetRequiredService<ISessionReminderHandoffWriter>(),
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
        scope.ServiceProvider.GetRequiredService<IClock>(),
        scope.ServiceProvider.GetRequiredService<InviteEmailService>());

    private static SessionParticipationUseCases BuildParticipationUseCases(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<ISessionRepository>(),
        scope.ServiceProvider.GetRequiredService<ISessionLockRepository>(),
        scope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
        scope.ServiceProvider.GetRequiredService<IBlockRepository>(),
        scope.ServiceProvider.GetRequiredService<IFriendGroupRepository>(),
        scope.ServiceProvider.GetRequiredService<ISessionNotificationMaterializer>(),
        scope.ServiceProvider.GetRequiredService<ISessionReminderHandoffWriter>(),
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
        scope.ServiceProvider.GetRequiredService<IClock>());

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

    private async Task<BlockGraphCommandResult> BlockAsync(
        AsyncServiceScope scope,
        UserId actorId,
        UserId targetId,
        IFriendPairLocker? locker = null)
    {
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string targetUsername = (await context.DomainUsers.AsNoTracking()
            .SingleAsync(u => u.Id == targetId)).Username!.Value.Value;

        var useCase = new BlockUserUseCase(
            scope.ServiceProvider.GetRequiredService<IUserGraphResolver>(),
            locker ?? scope.ServiceProvider.GetRequiredService<IFriendPairLocker>(),
            scope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
            scope.ServiceProvider.GetRequiredService<IBlockRepository>(),
            scope.ServiceProvider.GetRequiredService<IBlockEffectsExecutor>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            _factory.Clock);
        return await useCase.ExecuteAsync(actorId, targetUsername, default);
    }

    /// <summary>
    /// <see cref="PartyUpApiFactory.SeedUserAsync"/> derives its default username from the first 8 hex
    /// characters of the (version-7, timestamp-prefixed) user id, so two users seeded within the same
    /// test can collide on username when created milliseconds apart. Block resolution goes by
    /// username, so every test here that blocks needs distinct ones — mirrors the
    /// <c>ChangeUsername</c>/<c>UsernameRegistryEntry.ClaimNew</c> pattern
    /// <c>Friends/DeletionFriendRaceTests.cs</c> already uses for the same reason.
    /// </summary>
    private async Task EnsureDistinctUsernamesAsync(params UserId[] userIds)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string batchSuffix = Guid.NewGuid().ToString("N");

        for (int i = 0; i < userIds.Length; i++)
        {
            User user = await context.DomainUsers.SingleAsync(u => u.Id == userIds[i]);
            Username username = Username.Create($"race{i}{batchSuffix[..14]}");
            user.ChangeUsername(username, _factory.Clock);
            context.UsernameRegistry.Add(UsernameRegistryEntry.ClaimNew(username, user.Id));
        }

        await context.SaveChangesAsync();
    }

    private SessionDetails SessionDetailsFor() => new(
        SessionTitle.Create("Attachment race session"),
        _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
        Duration15.FromMinutes(90),
        Platform.NotApplicable,
        null,
        SessionNotes.Empty,
        minAttendance: 1,
        maxCapacity: null,
        visibility: (true ? PartyUp.Domain.Kernel.ContentVisibility.Private : PartyUp.Domain.Kernel.ContentVisibility.Public));
}

internal sealed class AttemptGate
{
    internal TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Signals the moment the attachment command's own bulk user lock is attempted, before
/// awaiting the (blocked) call, so the test can assert the caller is genuinely contended rather than
/// merely slow.</summary>
internal sealed class AttemptingSessionLockRepository(
    ISessionLockRepository inner,
    AttemptGate gate) : ISessionLockRepository
{
    public Task LockUsersAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken)
    {
        Task attempt = inner.LockUsersAsync(userIds, cancellationToken);
        gate.Attempted.TrySetResult();
        return attempt;
    }

    public Task LockSessionsAsync(IReadOnlyCollection<SessionId> sessionIds, CancellationToken cancellationToken) =>
        inner.LockSessionsAsync(sessionIds, cancellationToken);
}
