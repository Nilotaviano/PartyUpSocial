using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class ProductionUserGraphResolverTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();

    public Task InitializeAsync() => _database.StartAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task ResolveProfile_HidesABlockedGraphRow_D065()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 8, 1, 18, 0));
        User[] users = await _database.SeedUsersAsync(2, clock);
        User viewer = users[0];
        User subject = users[1];

        await using AppDbContext context = _database.CreateContext();
        context.Blocks.Add(Block.Create(viewer.Id, subject.Id, clock));
        await context.SaveChangesAsync();
        var resolver = new UserGraphResolver(context);

        ResolvedUserGraph? result = await resolver.ResolveProfileByFriendCodeAsync(
            viewer.Id,
            subject.FriendCode!.Value.Value,
            default);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveProfile_HidesTheSubjectWhoBlockedTheViewer_D065()
    {
        var clock = new MutableClock(Instant.FromUtc(2026, 8, 1, 18, 0));
        User[] users = await _database.SeedUsersAsync(2, clock);
        User viewer = users[0];
        User subject = users[1];

        await using AppDbContext context = _database.CreateContext();
        context.Blocks.Add(Block.Create(subject.Id, viewer.Id, clock));
        await context.SaveChangesAsync();
        var resolver = new UserGraphResolver(context);

        ResolvedUserGraph? result = await resolver.ResolveProfileByFriendCodeAsync(
            viewer.Id,
            subject.FriendCode!.Value.Value,
            default);

        Assert.Null(result);
    }
}
