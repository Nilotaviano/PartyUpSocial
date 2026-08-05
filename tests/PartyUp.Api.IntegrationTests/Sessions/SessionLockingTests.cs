using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// P4-LK-01 (PHASE4_DESIGN.md §6.1) proven through the real S2 command surface, not raw SQL: every
/// attachment command locks the session parent before it ever reads occupancy, and a multi-user lock
/// set is always acquired in the database's declared A-068 order — never the order a caller happened to
/// build its collection in. <see cref="SessionRepositoryLockTests"/> (S1) proves the same two rules at
/// the repository/lock-adapter layer with raw SQL; this file proves the same guarantees hold once the
/// real S2 use cases (<see cref="SessionInvitationUseCases"/>, <see cref="SessionJoinRequestUseCases"/>)
/// are the caller.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionLockingTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    public static IEnumerable<object[]> AttachmentCommands()
    {
        yield return [AttachmentCommand.Invite];
        yield return [AttachmentCommand.Approve];
    }

    /// <summary>
    /// For invite and for join-request approval alike: while a concurrent transaction holds the
    /// session parent's row lock, the attachment command's occupancy read (its
    /// <c>LoadForUpdateAsync</c> child load) cannot run ahead of it. The blocking write below happens
    /// entirely inside the held transaction, so a command that (incorrectly) read children before the
    /// parent lock cleared would see a stale roster and return before the holder's write is visible.
    /// </summary>
    [Theory]
    [MemberData(nameof(AttachmentCommands))]
    public async Task ParentLockPrecedesEveryOccupancyRead_D084(AttachmentCommand command)
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"lock-cmd-organizer-{command}@example.test");
        UserId counterpartId = await _factory.SeedUserAsync(email: $"lock-cmd-counterpart-{command}@example.test");
        UserId thirdUserId = await _factory.SeedUserAsync(email: $"lock-cmd-third-{command}@example.test");

        SessionId sessionId;
        JoinRequestId? joinRequestId = null;

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            SessionDetails details = SessionDetailsFor();
            Session session = Session.Create(organizerId, details, [], _factory.Clock);
            _ = session.DequeueEvents();
            sessionId = session.Id;

            if (command is AttachmentCommand.Invite)
            {
                Friendship friendship = Friendship.Request(
                    await context.DomainUsers.SingleAsync(u => u.Id == organizerId),
                    await context.DomainUsers.SingleAsync(u => u.Id == counterpartId),
                    _factory.Clock);
                friendship.Accept(counterpartId, _factory.Clock);
                _ = friendship.DequeueEvents();
                context.Friendships.Add(friendship);
            }
            else
            {
                JoinRequest request = session.OpenJoinRequest(counterpartId, _factory.Clock);
                _ = session.DequeueEvents();
                joinRequestId = request.Id;
            }

            context.Sessions.Add(session);
            await context.SaveChangesAsync();
        }

        using SemaphoreSlim writerHolding = new(0, 1);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task writer = Task.Run(async () =>
        {
            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM sessions WHERE id = {sessionId.Value} FOR NO KEY UPDATE");
            writerHolding.Release();
            await release.Task;

            // Mutates a child row while still holding the uncommitted parent lock: a command whose
            // occupancy read ran ahead of the parent lock would miss this row.
            Session tracked = await context.Sessions
                .Include(s => s.Participations)
                .SingleAsync(s => s.Id == sessionId);
            tracked.Invite(thirdUserId, _factory.Clock);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        });

        await writerHolding.WaitAsync(TimeSpan.FromSeconds(10));

        Task commandTask;
        await using (AsyncServiceScope commandScope = _factory.Services.CreateAsyncScope())
        {
            commandTask = command switch
            {
                AttachmentCommand.Invite => RunInviteAsync(commandScope, sessionId, organizerId, counterpartId),
                _ => RunApproveAsync(commandScope, sessionId, organizerId, joinRequestId!.Value),
            };

            await Task.Delay(TimeSpan.FromMilliseconds(400));
            Assert.False(commandTask.IsCompleted, "The command must still be blocked on the held parent lock.");

            release.TrySetResult();
            await commandTask;
        }

        await writer;

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Session reloaded = await verify.Sessions.Include(s => s.Participations)
            .AsNoTracking()
            .SingleAsync(s => s.Id == sessionId);
        Assert.Contains(reloaded.Participations, p => p.UserId == thirdUserId);
        Assert.Contains(reloaded.Participations, p => p.UserId == counterpartId);
    }

    private static Task<SessionCommandResult> RunInviteAsync(
        AsyncServiceScope scope,
        SessionId sessionId,
        UserId organizerId,
        UserId counterpartId)
    {
        var useCase = new SessionInvitationUseCases(
            scope.ServiceProvider.GetRequiredService<ISessionRepository>(),
            scope.ServiceProvider.GetRequiredService<ISessionLockRepository>(),
            scope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
            scope.ServiceProvider.GetRequiredService<IBlockRepository>(),
            scope.ServiceProvider.GetRequiredService<ISessionNotificationMaterializer>(),
            scope.ServiceProvider.GetRequiredService<ISessionReminderHandoffWriter>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IClock>());
        return useCase.InviteRegisteredUsersAsync(sessionId, organizerId, [counterpartId], default);
    }

    private static Task<JoinRequestCommandResult> RunApproveAsync(
        AsyncServiceScope scope,
        SessionId sessionId,
        UserId organizerId,
        JoinRequestId joinRequestId)
    {
        var useCase = new SessionJoinRequestUseCases(
            scope.ServiceProvider.GetRequiredService<ISessionRepository>(),
            scope.ServiceProvider.GetRequiredService<ISessionReadRepository>(),
            scope.ServiceProvider.GetRequiredService<ISessionLockRepository>(),
            scope.ServiceProvider.GetRequiredService<ISessionNotificationMaterializer>(),
            scope.ServiceProvider.GetRequiredService<ISessionReminderHandoffWriter>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IClock>());
        return useCase.ApproveJoinRequestAsync(sessionId, organizerId, joinRequestId, default);
    }

    /// <summary>
    /// Deterministic proof, not a timing-luck race (per the S1
    /// <c>SessionRepositoryLockTests.MultiRowLocksAreOrderedByTheDatabaseNotTheCaller_A068</c>
    /// precedent): <see cref="SessionInvitationUseCases.InviteRegisteredUsersAsync"/> builds its lock
    /// set as <c>{organizerId, invitee1, invitee2}</c> and hands the whole set to
    /// <c>ISessionLockRepository.LockUsersAsync</c> in one statement — A-068 declares
    /// <c>users -> ORDER BY id</c>, so Postgres, not the set's enumeration order, decides which id is
    /// acquired first. This holds the higher-id invitee's row locked in one transaction, then invites
    /// with that invitee listed <em>before</em> the lower-id one in the input list — the opposite of
    /// the declared DB order — and proves the lower-id row is already locked (via a failing
    /// <c>NOWAIT</c> probe) before the caller ever blocks on the higher one.
    /// </summary>
    [Fact]
    public async Task BulkInviteAndApprovalLockInDatabaseOrderNotInputOrder_A068()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "bulk-lock-organizer@example.test");
        UserId userA = await _factory.SeedUserAsync(email: "bulk-lock-a@example.test");
        UserId userB = await _factory.SeedUserAsync(email: "bulk-lock-b@example.test");

        int order = userA.Value.ToByteArray(bigEndian: true).AsSpan()
            .SequenceCompareTo(userB.Value.ToByteArray(bigEndian: true));
        (UserId lowerId, UserId higherId) = order < 0 ? (userA, userB) : (userB, userA);

        SessionId sessionId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Session session = Session.Create(organizerId, SessionDetailsFor(), [], _factory.Clock);
            _ = session.DequeueEvents();
            sessionId = session.Id;
            context.Sessions.Add(session);

            foreach (UserId invitee in new[] { lowerId, higherId })
            {
                Friendship friendship = Friendship.Request(
                    await context.DomainUsers.SingleAsync(u => u.Id == organizerId),
                    await context.DomainUsers.SingleAsync(u => u.Id == invitee),
                    _factory.Clock);
                friendship.Accept(invitee, _factory.Clock);
                _ = friendship.DequeueEvents();
                context.Friendships.Add(friendship);
            }

            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM users WHERE id = {higherId.Value} FOR NO KEY UPDATE");

        await using AsyncServiceScope callerScope = _factory.Services.CreateAsyncScope();
        // Deliberately lists the already-locked higher-id invitee first — the exact opposite of the
        // DB's declared ORDER BY id — so a caller-order implementation would block on the higher row
        // immediately without ever touching the lower one.
        Task<SessionCommandResult> callerTask = new SessionInvitationUseCases(
                callerScope.ServiceProvider.GetRequiredService<ISessionRepository>(),
                callerScope.ServiceProvider.GetRequiredService<ISessionLockRepository>(),
                callerScope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
                callerScope.ServiceProvider.GetRequiredService<IBlockRepository>(),
                callerScope.ServiceProvider.GetRequiredService<ISessionNotificationMaterializer>(),
                callerScope.ServiceProvider.GetRequiredService<ISessionReminderHandoffWriter>(),
                callerScope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                callerScope.ServiceProvider.GetRequiredService<IClock>())
            .InviteRegisteredUsersAsync(sessionId, organizerId, [higherId, lowerId], default);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(callerTask.IsCompleted, "The bulk lock must still be blocked on the contended higher-id row.");

        await using (AsyncServiceScope probeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext probeContext = probeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var probeTransaction = await probeContext.Database.BeginTransactionAsync();

            Exception? thrown = await Record.ExceptionAsync(() =>
                probeContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM users WHERE id = {lowerId.Value} FOR NO KEY UPDATE NOWAIT"));

            Assert.True(
                thrown is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.LockNotAvailable },
                "The lower-id invitee must already be locked by the blocked bulk-invite call — A-068 " +
                "orders by id, so the lower row is acquired before the caller blocks on the higher one.");

            await probeTransaction.RollbackAsync();
        }

        await holderTransaction.RollbackAsync();
        SessionCommandResult result = await callerTask;
        Assert.True(result.Succeeded);
    }

    private SessionDetails SessionDetailsFor() => new(
        SessionTitle.Create("Locking test session"),
        _factory.Clock.GetCurrentInstant() + Duration.FromHours(2),
        Duration15.FromMinutes(60),
        Platform.NotApplicable,
        null,
        SessionNotes.Empty,
        minAttendance: 1,
        maxCapacity: null,
        isPrivate: true);

    public enum AttachmentCommand
    {
        Invite,
        Approve,
    }
}
