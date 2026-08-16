using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Application.Polls;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Friends;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using PartyUp.Api.IntegrationTests.Polls;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// S2a replaced the Phase 3 <c>EmptyBlockEffectScopeProvider</c> placeholder with the real
/// <see cref="SessionBlockEffectScopeProvider"/> on the same <see cref="IBlockEffectScopeProvider"/>
/// port. This proves the real scope actually feeds <see cref="BlockEffectsExecutor"/> real session
/// state (not the placeholder's permanently empty scope) and that its A-068-ordered
/// <c>LockSessionsAsync</c> statement lets two blocks that share session parents proceed without
/// deadlocking each other.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionBlockScopeTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RealScope_InvokesExistingExecutorOnce_D077()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "scope-organizer@example.test");
        UserId participantId = await _factory.SeedUserAsync(email: "scope-participant@example.test");
        await EnsureDistinctUsernamesAsync(organizerId, participantId);

        SessionId sessionId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Session session = Session.Create(organizerId, SessionDetailsFor(), [], _factory.Clock);
            session.Invite(participantId, _factory.Clock);
            session.Rsvp(ParticipantRef.ForUser(participantId), ParticipationStatus.Going, _factory.Clock);
            _ = session.DequeueEvents();
            sessionId = session.Id;
            context.Sessions.Add(session);

            Friendship friendship = Friendship.Request(
                await context.DomainUsers.SingleAsync(u => u.Id == organizerId),
                await context.DomainUsers.SingleAsync(u => u.Id == participantId),
                _factory.Clock);
            friendship.Accept(participantId, _factory.Clock);
            _ = friendship.DequeueEvents();
            context.Friendships.Add(friendship);

            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope blockScope = _factory.Services.CreateAsyncScope();
        AppDbContext blockContext = blockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var counts = new ScopeLoadCounts();
        var countingScope = new CountingBlockEffectScopeProvider(
            new BlockEffectScopeProvider(
                blockContext,
                blockScope.ServiceProvider.GetRequiredService<ISessionLockRepository>(),
                blockScope.ServiceProvider.GetRequiredService<IPollLockRepository>()),
            counts);
        var executor = new BlockEffectsExecutor(
            countingScope,
            blockScope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
            blockScope.ServiceProvider.GetRequiredService<IReminderScheduleWriter>(),
            _factory.Clock,
            blockScope.ServiceProvider.GetRequiredService<ISessionNotificationMaterializer>());
        var useCase = new BlockUserUseCase(
            blockScope.ServiceProvider.GetRequiredService<IUserGraphResolver>(),
            blockScope.ServiceProvider.GetRequiredService<IFriendPairLocker>(),
            blockScope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
            blockScope.ServiceProvider.GetRequiredService<IBlockRepository>(),
            executor,
            blockScope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            _factory.Clock);

        string participantUsername = (await blockContext.DomainUsers.AsNoTracking()
            .SingleAsync(u => u.Id == participantId)).Username!.Value.Value;

        BlockGraphCommandResult result = await useCase.ExecuteAsync(organizerId, participantUsername, default);

        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(1, counts.Loads);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Participation participation = await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == sessionId && p.UserId == participantId);
        Assert.True(participation.IsRemoved);
        Assert.Equal(0, await verify.Friendships.CountAsync());
    }

    /// <summary>
    /// Two blocks that both organizer <paramref name="userA"/> issues — one against
    /// <paramref name="userB"/>, one against a third user — where both counterparts share the same two
    /// sessions. Without the A-068-declared single-statement, database-ordered
    /// <c>LockSessionsAsync</c>, two transactions each locking the same two session rows in
    /// caller-dependent order could deadlock; this proves they do not, by holding the first block's
    /// user-pair lock (which necessarily contends on the shared organizer row first) and only then
    /// releasing it, so the second block's own session-parent lock acquisition — which the first
    /// transaction has already released by then — proceeds cleanly.
    /// </summary>
    [Fact]
    public async Task ConcurrentBlocksSharingSessionsDoNotDeadlock_A068()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "shared-organizer@example.test");
        UserId counterpartOneId = await _factory.SeedUserAsync(email: "shared-counterpart-one@example.test");
        UserId counterpartTwoId = await _factory.SeedUserAsync(email: "shared-counterpart-two@example.test");
        await EnsureDistinctUsernamesAsync(organizerId, counterpartOneId, counterpartTwoId);

        SessionId sessionOneId;
        SessionId sessionTwoId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

            Session sessionOne = Session.Create(organizerId, SessionDetailsFor(), [], _factory.Clock);
            sessionOne.Invite(counterpartOneId, _factory.Clock);
            sessionOne.Rsvp(ParticipantRef.ForUser(counterpartOneId), ParticipationStatus.Going, _factory.Clock);
            sessionOne.Invite(counterpartTwoId, _factory.Clock);
            sessionOne.Rsvp(ParticipantRef.ForUser(counterpartTwoId), ParticipationStatus.Going, _factory.Clock);
            _ = sessionOne.DequeueEvents();
            sessionOneId = sessionOne.Id;

            Session sessionTwo = Session.Create(organizerId, SessionDetailsFor(), [], _factory.Clock);
            sessionTwo.Invite(counterpartOneId, _factory.Clock);
            sessionTwo.Rsvp(ParticipantRef.ForUser(counterpartOneId), ParticipationStatus.Going, _factory.Clock);
            sessionTwo.Invite(counterpartTwoId, _factory.Clock);
            sessionTwo.Rsvp(ParticipantRef.ForUser(counterpartTwoId), ParticipationStatus.Going, _factory.Clock);
            _ = sessionTwo.DequeueEvents();
            sessionTwoId = sessionTwo.Id;

            context.Sessions.AddRange(sessionOne, sessionTwo);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope firstScope = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = _factory.Services.CreateAsyncScope();
        var gate = new PairHoldGate();

        Task<BlockGraphCommandResult> firstBlock = BlockAsync(
            firstScope,
            organizerId,
            counterpartOneId,
            new HoldingFriendPairLocker(
                new FriendPairLocker(firstScope.ServiceProvider.GetRequiredService<AppDbContext>()),
                gate));
        await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var attemptGate = new PairAttemptGate();
        Task<BlockGraphCommandResult> secondBlock = BlockAsync(
            secondScope,
            organizerId,
            counterpartTwoId,
            new AttemptingFriendPairLocker(
                new FriendPairLocker(secondScope.ServiceProvider.GetRequiredService<AppDbContext>()),
                attemptGate));
        await attemptGate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(secondBlock.IsCompleted, "The second block must still be waiting on the shared organizer row.");

        gate.Release.TrySetResult();

        BlockGraphCommandResult first = await firstBlock.WaitAsync(TimeSpan.FromSeconds(10));
        BlockGraphCommandResult second = await secondBlock.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (SessionId sessionId in new[] { sessionOneId, sessionTwoId })
        {
            Assert.True((await verify.SessionParticipations.AsNoTracking().SingleAsync(p =>
                p.SessionId == sessionId && p.UserId == counterpartOneId)).IsRemoved);
            Assert.True((await verify.SessionParticipations.AsNoTracking().SingleAsync(p =>
                p.SessionId == sessionId && p.UserId == counterpartTwoId)).IsRemoved);
        }

        Assert.Equal(2, await verify.Blocks.CountAsync());
    }

    /// <summary>
    /// PHASE6_DESIGN.md §2.1: the renamed <see cref="BlockEffectScopeProvider"/>'s poll half feeds
    /// <see cref="BlockEffects.Apply"/> a real, non-Removed <see cref="PollParticipant"/> candidate —
    /// same fail-closed, DI-registered path <see cref="BlockUserUseCase"/> already uses in production.
    /// </summary>
    [Fact]
    public async Task RealScope_RemovesTheCounterpartsPollParticipant_D077()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-scope-organizer@example.test");
        UserId participantId = await _factory.SeedUserAsync(email: "poll-scope-participant@example.test");
        await EnsureDistinctUsernamesAsync(organizerId, participantId);

        PollId pollId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, participantId, _factory.Clock);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    organizerId,
                    "Block scope poll",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    OptionStartTimes: [now + Duration.FromHours(72), now + Duration.FromHours(96)],
                    DeadlineChoice: PollDeadlineChoice.Hours24,
                    InviteeUserIds: [participantId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            pollId = created.Poll!.Id;
        }

        await using AsyncServiceScope blockScope = _factory.Services.CreateAsyncScope();
        BlockGraphCommandResult result = await BlockAsync(
            blockScope,
            organizerId,
            participantId,
            blockScope.ServiceProvider.GetRequiredService<IFriendPairLocker>());

        Assert.True(result.Succeeded, result.Code);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        PollParticipant participant = await verify.PollParticipants.AsNoTracking()
            .SingleAsync(p => p.PollId == pollId && p.UserId == participantId);
        Assert.True(participant.IsRemoved);
    }

    /// <summary>
    /// Same port, the open-join-request arm: the counterpart never became a <see cref="PollParticipant"/>
    /// row, only a pending <see cref="JoinRequest"/> the block must still lapse.
    /// </summary>
    [Fact]
    public async Task RealScope_LapsesTheCounterpartsOpenPollJoinRequest_D077()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-scope-jr-organizer@example.test");
        UserId invitedId = await _factory.SeedUserAsync(email: "poll-scope-jr-invited@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: "poll-scope-jr-requester@example.test");
        await EnsureDistinctUsernamesAsync(organizerId, invitedId, requesterId);

        PollId pollId;
        JoinRequestId requestId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, invitedId, _factory.Clock);
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, requesterId, _factory.Clock);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    organizerId,
                    "Block scope join-request poll",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    OptionStartTimes: [now + Duration.FromHours(72), now + Duration.FromHours(96)],
                    DeadlineChoice: PollDeadlineChoice.Hours24,
                    InviteeUserIds: [invitedId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            pollId = created.Poll!.Id;

            PollJoinRequestCommandResult joined = await PollTestHelpers.BuildJoinRequestUseCases(seedScope)
                .RequestJoinAsync(pollId, requesterId, default);
            Assert.True(joined.Succeeded, joined.Code);
            requestId = joined.JoinRequest!.Id;
        }

        await using AsyncServiceScope blockScope = _factory.Services.CreateAsyncScope();
        BlockGraphCommandResult result = await BlockAsync(
            blockScope,
            organizerId,
            requesterId,
            blockScope.ServiceProvider.GetRequiredService<IFriendPairLocker>());

        Assert.True(result.Succeeded, result.Code);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        JoinRequest request = await verify.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
    }

    /// <summary>
    /// P3-LK-01/A-068: proves the poll-parent lock statement never runs before the session-parent lock
    /// statement in a single scope load, for a pair that qualifies on both kinds of parent — the
    /// concrete lock-order guarantee the concurrent-session test above already exercises for two
    /// session parents alone.
    /// </summary>
    [Fact]
    public async Task RealScope_LocksSessionParentsBeforePollParents()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-scope-order-organizer@example.test");
        UserId counterpartId = await _factory.SeedUserAsync(email: "poll-scope-order-counterpart@example.test");
        await EnsureDistinctUsernamesAsync(organizerId, counterpartId);

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Session session = Session.Create(organizerId, SessionDetailsFor(), [], _factory.Clock);
            session.Invite(counterpartId, _factory.Clock);
            session.Rsvp(ParticipantRef.ForUser(counterpartId), ParticipationStatus.Going, _factory.Clock);
            _ = session.DequeueEvents();
            context.Sessions.Add(session);
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, counterpartId, _factory.Clock);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    organizerId,
                    "Lock order poll",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    OptionStartTimes: [now + Duration.FromHours(72), now + Duration.FromHours(96)],
                    DeadlineChoice: PollDeadlineChoice.Hours24,
                    InviteeUserIds: [counterpartId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = new LockOrderRecorder();
        var provider = new BlockEffectScopeProvider(
            context2,
            new RecordingSessionLockRepository(
                new SessionLockRepository(context2), order),
            new RecordingPollLockRepository(
                new PollLockRepository(context2), order));

        await provider.LoadForUpdateAsync(organizerId, counterpartId, default);

        Assert.Equal(["session", "poll"], order.Calls);
    }

    private async Task<BlockGraphCommandResult> BlockAsync(
        AsyncServiceScope scope,
        UserId actorId,
        UserId targetId,
        IFriendPairLocker locker)
    {
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string targetUsername = (await context.DomainUsers.AsNoTracking()
            .SingleAsync(u => u.Id == targetId)).Username!.Value.Value;

        var useCase = new BlockUserUseCase(
            scope.ServiceProvider.GetRequiredService<IUserGraphResolver>(),
            locker,
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
    /// test can collide on username when created milliseconds apart. Block resolution goes by username,
    /// so every test that blocks by username needs distinct ones — mirrors the
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
            Username username = Username.Create($"blk{i}{batchSuffix[..14]}");
            user.ChangeUsername(username, _factory.Clock);
            context.UsernameRegistry.Add(UsernameRegistryEntry.ClaimNew(username, user.Id));
        }

        await context.SaveChangesAsync();
    }

    private SessionDetails SessionDetailsFor() => new(
        SessionTitle.Create("Shared block scope session"),
        _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
        Duration15.FromMinutes(90),
        Platform.NotApplicable,
        null,
        SessionNotes.Empty,
        minAttendance: 1,
        maxCapacity: null,
        visibility: ContentVisibility.Private);
}

internal sealed class ScopeLoadCounts
{
    private int _loads;

    internal int Loads => Volatile.Read(ref _loads);

    internal void RecordLoad() => Interlocked.Increment(ref _loads);
}

internal sealed class CountingBlockEffectScopeProvider(
    IBlockEffectScopeProvider inner,
    ScopeLoadCounts counts) : IBlockEffectScopeProvider
{
    public Task<BlockEffectScope> LoadForUpdateAsync(
        UserId first,
        UserId second,
        CancellationToken cancellationToken)
    {
        counts.RecordLoad();
        return inner.LoadForUpdateAsync(first, second, cancellationToken);
    }
}

internal sealed class PairHoldGate
{
    internal TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class PairAttemptGate
{
    internal TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Holds after acquiring the pair lock, so a second, independent pair-lock attempt on a
/// shared user blocks behind it — the real database contention point for two blocks sharing an
/// organizer.</summary>
internal sealed class HoldingFriendPairLocker(IFriendPairLocker inner, PairHoldGate gate) : IFriendPairLocker
{
    public async Task<LockedFriendPair?> LockAsync(UserId first, UserId second, CancellationToken cancellationToken)
    {
        LockedFriendPair? locked = await inner.LockAsync(first, second, cancellationToken);
        gate.Held.TrySetResult();
        await gate.Release.Task.WaitAsync(cancellationToken);
        return locked;
    }
}

internal sealed class AttemptingFriendPairLocker(IFriendPairLocker inner, PairAttemptGate gate) : IFriendPairLocker
{
    public async Task<LockedFriendPair?> LockAsync(UserId first, UserId second, CancellationToken cancellationToken)
    {
        Task<LockedFriendPair?> attempt = inner.LockAsync(first, second, cancellationToken);
        gate.Attempted.TrySetResult();
        return await attempt;
    }
}

/// <summary>Records which of the two lock kinds a single <c>LoadForUpdateAsync</c> call reaches first.</summary>
internal sealed class LockOrderRecorder
{
    private readonly List<string> _calls = [];

    internal IReadOnlyList<string> Calls => _calls;

    internal void Record(string kind) => _calls.Add(kind);
}

internal sealed class RecordingSessionLockRepository(ISessionLockRepository inner, LockOrderRecorder order)
    : ISessionLockRepository
{
    public Task LockUsersAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken) =>
        inner.LockUsersAsync(userIds, cancellationToken);

    public Task LockSessionsAsync(IReadOnlyCollection<SessionId> sessionIds, CancellationToken cancellationToken)
    {
        order.Record("session");
        return inner.LockSessionsAsync(sessionIds, cancellationToken);
    }
}

internal sealed class RecordingPollLockRepository(IPollLockRepository inner, LockOrderRecorder order)
    : IPollLockRepository
{
    public Task LockUsersAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken) =>
        inner.LockUsersAsync(userIds, cancellationToken);

    public Task LockPollsAsync(IReadOnlyCollection<PollId> pollIds, CancellationToken cancellationToken)
    {
        order.Record("poll");
        return inner.LockPollsAsync(pollIds, cancellationToken);
    }
}
