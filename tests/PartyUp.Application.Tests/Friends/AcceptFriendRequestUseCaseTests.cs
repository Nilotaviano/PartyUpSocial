using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;

namespace PartyUp.Application.Tests.Friends;

public sealed class AcceptFriendRequestUseCaseTests
{
    [Fact]
    public async Task Execute_AddresseeAcceptsSilentlyAfterPairGraphLocks_D015_D105()
    {
        var fixture = new FriendCommandFixture();
        fixture.SetExisting(Friendship.Request(fixture.Actor, fixture.Target, fixture.Clock));

        FriendGraphCommandResult result = await new AcceptFriendRequestUseCase(
                fixture.PairLocker,
                fixture.Friendships,
                fixture.Blocks,
                fixture.UnitOfWork,
                fixture.Clock)
            .ExecuteAsync(fixture.Target.Id, fixture.Friendships.Existing!.Id, default);

        Assert.True(result.Succeeded);
        Assert.Equal("accepted", result.Friendship!.Status);
        Assert.Equal("accepted", result.Friendship.Direction);
        Assert.Equal(fixture.Actor.Username!.Value.Value, result.Friendship.CounterpartUsername);
        Assert.Equal(["users", "friendship", "blocks"], fixture.Calls);
        Assert.Empty(fixture.Friendships.Existing!.DequeueEvents());
        Assert.Empty(fixture.Notifications.Added);
    }

    [Fact]
    public async Task Execute_RequesterIsHiddenAndAcceptedRowIsConflict_D015()
    {
        var wrongActor = new FriendCommandFixture();
        wrongActor.SetExisting(Friendship.Request(
            wrongActor.Actor,
            wrongActor.Target,
            wrongActor.Clock));

        FriendGraphCommandResult hidden = await new AcceptFriendRequestUseCase(
                wrongActor.PairLocker,
                wrongActor.Friendships,
                wrongActor.Blocks,
                wrongActor.UnitOfWork,
                wrongActor.Clock)
            .ExecuteAsync(
                wrongActor.Actor.Id,
                wrongActor.Friendships.Existing!.Id,
                default);

        Assert.Equal("friend_request_not_found", hidden.Code);

        var accepted = new FriendCommandFixture();
        Friendship row = Friendship.Request(accepted.Actor, accepted.Target, accepted.Clock);
        row.Accept(accepted.Target.Id, accepted.Clock);
        accepted.SetExisting(row);

        FriendGraphCommandResult conflict = await new AcceptFriendRequestUseCase(
                accepted.PairLocker,
                accepted.Friendships,
                accepted.Blocks,
                accepted.UnitOfWork,
                accepted.Clock)
            .ExecuteAsync(accepted.Target.Id, row.Id, default);

        Assert.Equal("friend_request_not_pending", conflict.Code);
    }
}
