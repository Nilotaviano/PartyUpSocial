using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class BlockConstraintTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();

    public Task InitializeAsync() => _database.StartAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task Insert_AllowsOnlyOneOrderedPairAndRejectsSelf_D065()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 8, 1, 15, 0));
        User[] users = await _database.SeedUsersAsync(2, clock);
        Block forward = Block.Create(
            users[0].Id,
            users[1].Id,
            users[1].Username!.Value,
            clock);

        await using (AppDbContext context = _database.CreateContext())
        {
            new BlockRepository(context).Add(forward);
            await context.SaveChangesAsync();
        }

        await using (AppDbContext verification = _database.CreateContext())
        {
            Block reloaded = await verification.Blocks.AsNoTracking().SingleAsync();
            Assert.Equal(forward.Id, reloaded.Id);
            Assert.Equal(forward.BlockerId, reloaded.BlockerId);
            Assert.Equal(forward.BlockedId, reloaded.BlockedId);
            Assert.Equal(forward.CreatedAt, reloaded.CreatedAt);
        }

        await using (AppDbContext duplicateContext = _database.CreateContext())
        {
            new BlockRepository(duplicateContext).Add(
                Block.Create(
                    users[0].Id,
                    users[1].Id,
                    users[1].Username!.Value,
                    clock));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());
        }

        await using (AppDbContext reverseContext = _database.CreateContext())
        {
            new BlockRepository(reverseContext).Add(
                Block.Create(
                    users[1].Id,
                    users[0].Id,
                    users[0].Username!.Value,
                    clock));
            await reverseContext.SaveChangesAsync();
        }

        await using AppDbContext selfContext = _database.CreateContext();
        await Assert.ThrowsAnyAsync<DbException>(() =>
            selfContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO blocks (
                    id, blocker_id, blocked_id, blocked_username_snapshot, created_at)
                VALUES ({Guid.CreateVersion7()}, {users[0].Id.Value},
                        {users[0].Id.Value}, {users[0].Username!.Value.Value},
                        {clock.GetCurrentInstant()})
                """));
    }
}
