using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Friends;

public sealed class ListFriendsUseCaseTests
{
    [Fact]
    public async Task Execute_ReturnsFullIdentityForAcceptedFriends_D032()
    {
        User friend = FriendTestData.Active("alex", "Alex");
        friend.LinkPlatform(Platform.Steam, GamingHandle.Create("alex-steam"));
        var repository = new FriendTestFriendshipRepository
        {
            Accepted = [new AcceptedFriendProjection(FriendshipId.New(), FriendTestData.Counterpart(friend))],
        };

        FriendList result = await new ListFriendsUseCase(repository)
            .ExecuteAsync(UserId.New(), default);

        FriendItem item = Assert.Single(result.Items);
        Assert.Equal(friend.Id, item.UserId);
        Assert.Equal("alex", item.Username);
        Assert.Equal("Alex", item.DisplayName);
        Assert.Equal(friend.Avatar, item.Avatar);
        Assert.Equal("alex-steam", Assert.Single(item.Platforms).Handle);
    }
}
