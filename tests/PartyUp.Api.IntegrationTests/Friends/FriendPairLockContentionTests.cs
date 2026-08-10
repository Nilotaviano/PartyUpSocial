using System.Diagnostics;
using NodaTime;
using Npgsql;
using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.Friends;

/// <summary>
/// A-069: the friend pair lock is bounded by <c>SET LOCAL lock_timeout</c>, so a concurrent holder of
/// the two <c>users</c> rows makes an accept either wait out the holder or abort as a retryable
/// <c>55P03</c> — never block until the ambient token is cancelled and escape as an
/// <see cref="OperationCanceledException"/>. The holder is a second, raw Npgsql connection taking the
/// same <c>FOR UPDATE</c> in an open transaction, so the contention is real rather than simulated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FriendPairLockContentionTests : IAsyncLifetime
{
    private static readonly TimeSpan LadderCeiling = TimeSpan.FromSeconds(10);

    private readonly FriendGraphDatabase _database = new();
    private readonly MutableClock _clock = new(Instant.FromUtc(2026, 8, 1, 20, 0));
    private User[] _users = [];
    private FriendshipId _requestId;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _users = await _database.SeedUsersAsync(2, _clock);
        await FriendCommandIntegration.EnsureUsernameRegistryAsync(_database, _users, _clock);

        Friendship request = Friendship.Request(_users[0], _users[1], _clock);
        request.DequeueEvents();
        await using AppDbContext context = _database.CreateContext();
        context.Friendships.Add(request);
        await context.SaveChangesAsync();
        _requestId = request.Id;
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    /// <summary>
    /// A holder that releases inside the lock timeout must not turn into a failure: the wait is
    /// bounded, not aggressive. Guards the timeout value itself — a too-short one breaks this.
    /// </summary>
    [Fact]
    public async Task AcceptWaitsOutAHolderThatReleasesInsideTheLockTimeout()
    {
        await using NpgsqlConnection holder = await OpenHolderAsync();
        await using NpgsqlTransaction holderTransaction = await holder.BeginTransactionAsync();
        await HoldPairRowsAsync(holder, holderTransaction);

        await using AppDbContext context = _database.CreateContext();
        Task<FriendGraphCommandResult> accept = AcceptAsync(context, CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(250));
        Assert.False(accept.IsCompleted);
        await holderTransaction.CommitAsync();

        FriendGraphCommandResult result = await accept.WaitAsync(LadderCeiling);
        Assert.True(result.Succeeded, result.Code);
        Assert.Equal("accepted", result.Friendship!.Status);
    }

    /// <summary>
    /// A holder that never releases exhausts A-069's ladder. The observable must be the bounded,
    /// well-typed <c>55P03</c> — proving both that the lock stopped waiting and that the retry
    /// predicate treated it as transient (three attempts span more than two lock timeouts).
    /// </summary>
    [Fact]
    public async Task AcceptUnderAnUnreleasedHolderAbortsAsLockNotAvailableRatherThanHanging()
    {
        await using NpgsqlConnection holder = await OpenHolderAsync();
        await using NpgsqlTransaction holderTransaction = await holder.BeginTransactionAsync();
        await HoldPairRowsAsync(holder, holderTransaction);

        // Stands in for the request-scoped cancellation that pre-fix converted an unbounded wait
        // into: if the lock still blocked, this token — not lock_timeout — would end the wait.
        using var requestBudget = new CancellationTokenSource(LadderCeiling);
        await using AppDbContext context = _database.CreateContext();

        long startedAt = Stopwatch.GetTimestamp();
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => AcceptAsync(context, requestBudget.Token));
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.Equal(PostgresErrorCodes.LockNotAvailable, exception.SqlState);
        Assert.True(
            elapsed >= TimeSpan.FromSeconds(2),
            $"expected three bounded attempts, but the call returned after {elapsed}");
        Assert.True(elapsed < LadderCeiling, $"the ladder did not bound the wait: {elapsed}");

        await holderTransaction.RollbackAsync();
    }

    private Task<FriendGraphCommandResult> AcceptAsync(
        AppDbContext context,
        CancellationToken cancellationToken) =>
        new AcceptFriendRequestUseCase(
                new FriendPairLocker(context),
                new FriendshipRepository(context),
                new BlockRepository(context),
                new UnitOfWork(context),
                _clock)
            .ExecuteAsync(_users[1].Id, _requestId, cancellationToken);

    private async Task<NpgsqlConnection> OpenHolderAsync()
    {
        var holder = new NpgsqlConnection(_database.ConnectionString);
        await holder.OpenAsync();
        return holder;
    }

    private async Task HoldPairRowsAsync(NpgsqlConnection holder, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM users WHERE id = ANY(@ids) ORDER BY id FOR UPDATE",
            holder,
            transaction);
        command.Parameters.AddWithValue(
            "ids",
            new[] { _users[0].Id.Value, _users[1].Id.Value });
        await command.ExecuteNonQueryAsync();
    }
}
