using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendCountQueryTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();

    public Task InitializeAsync() => _database.StartAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task AcceptedCount_IsQueryableFromBothCanonicalSides_D179()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 8, 1, 17, 0));
        User[] users = (await _database.SeedUsersAsync(8, clock))
            .OrderBy(user => user.Id.Value)
            .ToArray();
        User target = users[3];
        Friendship targetAsHigh = Accept(users[0], target, clock);
        Friendship targetAsLow = Accept(target, users[7], clock);
        Friendship unrelated = Accept(users[1], users[2], clock);
        Friendship pending = Friendship.Request(target, users[6], clock);

        await using AppDbContext context = _database.CreateContext();
        context.Friendships.AddRange(targetAsHigh, targetAsLow, unrelated, pending);
        await context.SaveChangesAsync();

        int loCount = await context.Friendships.CountAsync(friendship =>
            friendship.UserLoId == target.Id &&
            friendship.Status == FriendshipStatus.Accepted);
        int hiCount = await context.Friendships.CountAsync(friendship =>
            friendship.UserHiId == target.Id &&
            friendship.Status == FriendshipStatus.Accepted);
        Assert.Equal(2, loCount + hiCount);

        await context.Database.ExecuteSqlRawAsync("ANALYZE friendships");
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off");
        string loPlan = await ReadPlanAsync(
            context,
            """
            EXPLAIN (COSTS OFF)
            SELECT count(*)
            FROM friendships
            WHERE user_lo_id = @user_id AND status = 1
            """,
            target.Id);
        string hiPlan = await ReadPlanAsync(
            context,
            """
            EXPLAIN (COSTS OFF)
            SELECT count(*)
            FROM friendships
            WHERE user_hi_id = @user_id AND status = 1
            """,
            target.Id);

        Assert.Contains("ix_friendships_lo_status", loPlan, StringComparison.Ordinal);
        Assert.Contains("ix_friendships_hi_status", hiPlan, StringComparison.Ordinal);
    }

    private static Friendship Accept(User first, User second, MutableClock clock)
    {
        Friendship friendship = Friendship.Request(first, second, clock);
        clock.Advance(Duration.FromSeconds(1));
        friendship.Accept(friendship.AddresseeId, clock);
        return friendship;
    }

    private static async Task<string> ReadPlanAsync(
        AppDbContext context,
        string commandText,
        UserId userId)
    {
        DbConnection connection = context.Database.GetDbConnection();
        await FriendGraphDatabase.EnsureOpenAsync(connection);
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = commandText;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "user_id";
        parameter.Value = userId.Value;
        command.Parameters.Add(parameter);
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
