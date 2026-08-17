using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// P3-LK-01 for polls (PHASE6_DESIGN.md §2 "LOCKING"): the poll parent lock precedes every
/// occupancy/option-count read, and a multi-row lock is acquired in the database's declared order,
/// never the caller's -- the poll-side mirror of
/// <see cref="Sessions.SessionRepositoryLockTests"/>/<see cref="Sessions.SessionLockingTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollLockingTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task PollParentLockPrecedesEveryChildQuery()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-lock-organizer@example.test");
        UserId voterId = await _factory.SeedUserAsync(email: "poll-lock-voter@example.test");

        PollId pollId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, voterId, _factory.Clock);
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
                    Visibility: ContentVisibility.Private,
            AudienceGroupIds: null,
                    OptionStartTimes: [now + Duration.FromHours(2), now + Duration.FromHours(3)],
                    DeadlineChoice: PollDeadlineChoice.Hours48,
                    InviteeUserIds: [],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            pollId = created.Poll!.Id;
        }

        using SemaphoreSlim writerLockAcquired = new(0, 1);

        Task writer = Task.Run(async () =>
        {
            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM polls WHERE id = {pollId.Value} FOR NO KEY UPDATE");
            writerLockAcquired.Release();

            // Mutate a child row (adds a suggested option) while still holding the uncommitted parent
            // lock: a reader whose child query ran ahead of the lock would miss it entirely.
            Poll tracked = await context.Polls
                .Include(p => p.Options)
                .Include(p => p.Participants)
                .SingleAsync(p => p.Id == pollId);
            tracked.SuggestOption(ParticipantRef.ForUser(organizerId), tracked.Options[0].StartTime + Duration.FromHours(10), Duration15.FromMinutes(PollSpecs.DefaultDurationMinutes), _factory.Clock);
            await context.SaveChangesAsync();

            await Task.Delay(TimeSpan.FromMilliseconds(400));
            await transaction.CommitAsync();
        });

        await writerLockAcquired.WaitAsync();

        Stopwatch stopwatch = Stopwatch.StartNew();
        PollForUpdate? reloaded;
        await using (AsyncServiceScope readerScope = _factory.Services.CreateAsyncScope())
        {
            var repository = readerScope.ServiceProvider.GetRequiredService<IPollRepository>();
            reloaded = await repository.LoadForUpdateAsync(pollId, default);
        }

        stopwatch.Stop();
        await writer;

        Assert.NotNull(reloaded);
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 300,
            $"LoadForUpdateAsync returned after only {stopwatch.ElapsedMilliseconds} ms; it must block on the " +
            "writer's parent lock rather than reading children ahead of it.");
        Assert.Equal(3, reloaded!.Poll.Options.Count);
    }

    /// <summary>
    /// Deterministic proof (not timing-luck, mirroring the S1 session precedent): A-068 declares
    /// <c>polls -&gt; ORDER BY id</c>, so a correct single-statement <c>LockPollsAsync</c> always
    /// acquires the lower-id row first regardless of caller array order.
    /// </summary>
    [Fact]
    public async Task MultiRowPollLocksAreOrderedByTheDatabaseNotTheCaller()
    {
        UserId organizerA = await _factory.SeedUserAsync(email: "poll-lock-order-a@example.test");
        UserId organizerB = await _factory.SeedUserAsync(email: "poll-lock-order-b@example.test");

        PollId pollA = await CreateBarePollAsync(organizerA);
        PollId pollB = await CreateBarePollAsync(organizerB);

        int order = pollA.Value.ToByteArray(bigEndian: true).AsSpan()
            .SequenceCompareTo(pollB.Value.ToByteArray(bigEndian: true));
        (PollId lowerId, PollId higherId) = order < 0 ? (pollA, pollB) : (pollB, pollA);

        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM polls WHERE id = {higherId.Value} FOR NO KEY UPDATE");

        await using AsyncServiceScope callerScope = _factory.Services.CreateAsyncScope();
        var repository = callerScope.ServiceProvider.GetRequiredService<IPollLockRepository>();
        AppDbContext callerContext = callerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var callerTransaction = await callerContext.Database.BeginTransactionAsync();

        Task callerLock = repository.LockPollsAsync([higherId, lowerId], default);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(callerLock.IsCompleted, "The multi-row lock must still be blocked on the contended higher-id row.");

        await using (AsyncServiceScope probeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext probeContext = probeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var probeTransaction = await probeContext.Database.BeginTransactionAsync();

            Exception? thrown = await Record.ExceptionAsync(() =>
                probeContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM polls WHERE id = {lowerId.Value} FOR NO KEY UPDATE NOWAIT"));

            Assert.True(
                thrown is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable },
                "The lower-id poll row must already be locked by the blocked caller -- A-068 orders by id, " +
                "so the lower row is acquired before the caller blocks on the higher one.");

            await probeTransaction.RollbackAsync();
        }

        await holderTransaction.RollbackAsync();
        await callerLock;
        await callerTransaction.CommitAsync();
    }

    /// <summary>
    /// The real S2 command surface, not raw SQL: <see cref="PollVoteUseCases.VoteAsync"/> blocks on
    /// the poll parent lock before it can read the participant/option occupancy it needs.
    /// </summary>
    [Fact]
    public async Task VoteCommandBlocksOnAnExternallyHeldPollParentLock()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-lock-cmd-organizer@example.test");
        UserId voterId = await _factory.SeedUserAsync(email: "poll-lock-cmd-voter@example.test");

        PollId pollId;
        PollOptionId optionId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, voterId, _factory.Clock);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    organizerId,
                    "Vote lock poll",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    OptionStartTimes: [now + Duration.FromHours(2), now + Duration.FromHours(3)],
                    DeadlineChoice: PollDeadlineChoice.Hours48,
                    InviteeUserIds: [voterId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            pollId = created.Poll!.Id;
            optionId = created.Poll!.Options[0].Id;
        }

        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM polls WHERE id = {pollId.Value} FOR NO KEY UPDATE");

        await using AsyncServiceScope commandScope = _factory.Services.CreateAsyncScope();
        Task<PollCommandResult> voteTask = PollTestHelpers.BuildVoteUseCases(commandScope)
            .VoteAsync(pollId, voterId, optionId, VoteChoice.Available, default);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(voteTask.IsCompleted, "Vote must still be blocked on the externally held poll parent lock.");

        await holderTransaction.RollbackAsync();
        PollCommandResult result = await voteTask;
        Assert.True(result.Succeeded, result.Code);
    }

    /// <summary>
    /// The users-before-poll ordering half of P3-LK-01 that <see cref="VoteCommandBlocksOnAnExternallyHeldPollParentLock"/>
    /// cannot reach (that test's poll is contended, so it can't tell whether the blocking came from
    /// the explicit <c>LockUsersAsync</c> call or from <c>LoadForUpdateAsync</c>'s own poll-row lock).
    /// Here the poll is free and only the voter's <c>users</c> row is externally held: <see cref="PollVoteUseCases.VoteAsync"/>
    /// must still block, proving the explicit <c>LockUsersAsync([actorId], ct)</c> call is itself
    /// load-bearing. Removing that call would let the vote proceed uncontended (the poll is free) and
    /// this test would go from red-on-purpose to green for the wrong reason -- it would complete well
    /// under the 300 ms window instead of blocking, and the assertion below would fail.
    /// </summary>
    [Fact]
    public async Task VoteCommandBlocksOnAnExternallyHeldUsersLock()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-lock-users-organizer@example.test");
        UserId voterId = await _factory.SeedUserAsync(email: "poll-lock-users-voter@example.test");

        PollId pollId;
        PollOptionId optionId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, voterId, _factory.Clock);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    organizerId,
                    "Users-lock order poll",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    OptionStartTimes: [now + Duration.FromHours(2), now + Duration.FromHours(3)],
                    DeadlineChoice: PollDeadlineChoice.Hours48,
                    InviteeUserIds: [voterId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            pollId = created.Poll!.Id;
            optionId = created.Poll!.Options[0].Id;
        }

        // Only the voter's users row is held -- the poll itself is completely free, isolating
        // LockUsersAsync as the sole thing the vote command can be blocking on.
        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM users WHERE id = {voterId.Value} FOR NO KEY UPDATE");

        await using AsyncServiceScope commandScope = _factory.Services.CreateAsyncScope();
        Task<PollCommandResult> voteTask = PollTestHelpers.BuildVoteUseCases(commandScope)
            .VoteAsync(pollId, voterId, optionId, VoteChoice.Available, default);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(voteTask.IsCompleted, "Vote must still be blocked on the externally held voter users row.");

        await holderTransaction.RollbackAsync();
        PollCommandResult result = await voteTask;
        Assert.True(result.Succeeded, result.Code);
    }

    private async Task<PollId> CreateBarePollAsync(UserId organizerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Instant now = _factory.Clock.GetCurrentInstant();
        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Bare poll",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                Visibility: ContentVisibility.Private,
            AudienceGroupIds: null,
                OptionStartTimes: [now + Duration.FromHours(2), now + Duration.FromHours(3)],
                DeadlineChoice: PollDeadlineChoice.Hours48,
                InviteeUserIds: [],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);
        return created.Poll!.Id;
    }
}
