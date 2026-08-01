using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendshipConstraintTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();

    public Task InitializeAsync() => _database.StartAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task Insert_RejectsBothOrientationsOfTheSamePair_D015()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 8, 1, 12, 0));
        User[] users = await _database.SeedUsersAsync(2, clock);
        Friendship first = Friendship.Request(users[0], users[1], clock);
        clock.Advance(Duration.FromMinutes(3));
        first.Accept(first.AddresseeId, clock);

        await using (AppDbContext context = _database.CreateContext())
        {
            context.Friendships.Add(first);
            await context.SaveChangesAsync();
        }

        await using (AppDbContext verification = _database.CreateContext())
        {
            Friendship reloaded = await verification.Friendships.AsNoTracking().SingleAsync();
            Assert.Equal(first.Id, reloaded.Id);
            Assert.Equal(first.UserLoId, reloaded.UserLoId);
            Assert.Equal(first.UserHiId, reloaded.UserHiId);
            Assert.Equal(first.RequestedBy, reloaded.RequestedBy);
            Assert.Equal(FriendshipStatus.Accepted, reloaded.Status);
            Assert.Equal(first.RequestedAt, reloaded.RequestedAt);
            Assert.Equal(first.AcceptedAt, reloaded.AcceptedAt);
        }

        Friendship reverse = Friendship.Request(users[1], users[0], clock);
        await using AppDbContext duplicateContext = _database.CreateContext();
        duplicateContext.Friendships.Add(reverse);
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Insert_RejectsEveryInvalidFriendshipShape_D015()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 8, 1, 13, 0));
        User[] users = (await _database.SeedUsersAsync(3, clock))
            .OrderBy(user => user.Id.Value)
            .ToArray();
        Instant acceptedAt = clock.GetCurrentInstant();

        await AssertInvalidInsertAsync(
            users[1].Id,
            users[1].Id,
            users[1].Id,
            1,
            acceptedAt);
        await AssertInvalidInsertAsync(
            users[0].Id,
            users[1].Id,
            users[2].Id,
            1,
            acceptedAt);
        await AssertInvalidInsertAsync(
            users[0].Id,
            users[1].Id,
            users[0].Id,
            2,
            acceptedAt);
        await AssertInvalidInsertAsync(
            users[0].Id,
            users[1].Id,
            users[0].Id,
            0,
            acceptedAt);

        await using AppDbContext context = _database.CreateContext();
        await Assert.ThrowsAnyAsync<DbException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO friendships
                    (id, user_lo_id, user_hi_id, requested_by, status, requested_at, accepted_at)
                VALUES
                    ({Guid.CreateVersion7()}, {users[0].Id.Value}, {users[1].Id.Value},
                     {users[0].Id.Value}, {(short)1}, {clock.GetCurrentInstant()}, NULL)
                """));
    }

    [Fact]
    public async Task Update_UsesXminForOptimisticConcurrency_D015()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 8, 1, 14, 0));
        User[] users = await _database.SeedUsersAsync(2, clock);
        Friendship friendship = Friendship.Request(users[0], users[1], clock);
        await using (AppDbContext setup = _database.CreateContext())
        {
            setup.Friendships.Add(friendship);
            await setup.SaveChangesAsync();
        }

        await using AppDbContext firstContext = _database.CreateContext();
        await using AppDbContext staleContext = _database.CreateContext();
        Friendship first = await firstContext.Friendships.SingleAsync();
        Friendship stale = await staleContext.Friendships.SingleAsync();
        clock.Advance(Duration.FromMinutes(1));
        first.Accept(first.AddresseeId, clock);
        await firstContext.SaveChangesAsync();
        stale.Accept(stale.AddresseeId, clock);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => staleContext.SaveChangesAsync());
    }

    private async Task AssertInvalidInsertAsync(
        UserId userLoId,
        UserId userHiId,
        UserId requestedBy,
        short status,
        Instant acceptedAt)
    {
        await using AppDbContext context = _database.CreateContext();
        await Assert.ThrowsAnyAsync<DbException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO friendships
                    (id, user_lo_id, user_hi_id, requested_by, status, requested_at, accepted_at)
                VALUES
                    ({Guid.CreateVersion7()}, {userLoId.Value}, {userHiId.Value},
                     {requestedBy.Value}, {status}, {acceptedAt}, {acceptedAt})
                """));
    }
}
