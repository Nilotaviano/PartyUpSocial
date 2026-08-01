using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;

namespace PartyUp.Application.Tests.Friends;

public sealed class UnfriendUseCaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_AllowsEitherPartyAndChangesNoContentScope_D065(
        bool counterpartActs)
    {
        var fixture = new FriendCommandFixture();
        Friendship row = Friendship.Request(fixture.Actor, fixture.Target, fixture.Clock);
        row.Accept(fixture.Target.Id, fixture.Clock);
        fixture.SetExisting(row);

        FriendGraphCommandResult result = await new UnfriendUseCase(
                fixture.PairLocker,
                fixture.Friendships,
                fixture.Blocks,
                fixture.UnitOfWork)
            .ExecuteAsync(
                counterpartActs ? fixture.Target.Id : fixture.Actor.Id,
                row.Id,
                default);

        Assert.True(result.Succeeded);
        Assert.Null(fixture.Friendships.Existing);
        Assert.Equal(["users", "friendship", "blocks"], fixture.Calls);
        Assert.Empty(fixture.Notifications.Added);
    }

    [Fact]
    public async Task Execute_CreatesNoNotification_D105()
    {
        var fixture = new FriendCommandFixture();
        Friendship row = Friendship.Request(fixture.Actor, fixture.Target, fixture.Clock);
        row.Accept(fixture.Target.Id, fixture.Clock);
        fixture.SetExisting(row);

        FriendGraphCommandResult result = await new UnfriendUseCase(
                fixture.PairLocker,
                fixture.Friendships,
                fixture.Blocks,
                fixture.UnitOfWork)
            .ExecuteAsync(fixture.Actor.Id, row.Id, default);

        Assert.True(result.Succeeded);
        Assert.Empty(fixture.Notifications.Added);
    }

    [Fact]
    public async Task Execute_PendingRowUsesSameFriendshipNotFoundShape_D015()
    {
        var fixture = new FriendCommandFixture();
        Friendship row = Friendship.Request(fixture.Actor, fixture.Target, fixture.Clock);
        fixture.SetExisting(row);

        FriendGraphCommandResult result = await new UnfriendUseCase(
                fixture.PairLocker,
                fixture.Friendships,
                fixture.Blocks,
                fixture.UnitOfWork)
            .ExecuteAsync(fixture.Actor.Id, row.Id, default);

        Assert.Equal("friendship_not_found", result.Code);
        Assert.Same(row, fixture.Friendships.Existing);
    }
}
