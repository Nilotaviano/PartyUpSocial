using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Tests.Friends;

public sealed class RejectFriendRequestUseCaseTests
{
    [Fact]
    public async Task Execute_DeletesTheIncomingRowSilently_D105()
    {
        var fixture = new FriendCommandFixture();
        fixture.SetExisting(Friendship.Request(fixture.Actor, fixture.Target, fixture.Clock));
        FriendshipId id = fixture.Friendships.Existing!.Id;

        FriendGraphCommandResult result = await new RejectFriendRequestUseCase(
                fixture.PairLocker,
                fixture.Friendships,
                fixture.Blocks,
                fixture.UnitOfWork)
            .ExecuteAsync(fixture.Target.Id, id, default);

        Assert.True(result.Succeeded);
        Assert.Null(result.Friendship);
        Assert.Null(fixture.Friendships.Existing);
        Assert.Equal(["users", "friendship", "blocks"], fixture.Calls);
        Assert.Empty(fixture.Notifications.Added);
    }

    [Fact]
    public async Task Execute_RequesterIsHiddenAndAcceptedRowIsConflict_D015()
    {
        var fixture = new FriendCommandFixture();
        fixture.SetExisting(Friendship.Request(fixture.Actor, fixture.Target, fixture.Clock));
        FriendshipId id = fixture.Friendships.Existing!.Id;
        var useCase = new RejectFriendRequestUseCase(
            fixture.PairLocker,
            fixture.Friendships,
            fixture.Blocks,
            fixture.UnitOfWork);

        FriendGraphCommandResult hidden = await useCase.ExecuteAsync(fixture.Actor.Id, id, default);

        Assert.Equal("friend_request_not_found", hidden.Code);

        var accepted = new FriendCommandFixture();
        Friendship row = Friendship.Request(accepted.Actor, accepted.Target, accepted.Clock);
        row.Accept(accepted.Target.Id, accepted.Clock);
        accepted.SetExisting(row);
        FriendGraphCommandResult conflict = await new RejectFriendRequestUseCase(
                accepted.PairLocker,
                accepted.Friendships,
                accepted.Blocks,
                accepted.UnitOfWork)
            .ExecuteAsync(accepted.Target.Id, row.Id, default);

        Assert.Equal("friend_request_not_pending", conflict.Code);
    }
}
