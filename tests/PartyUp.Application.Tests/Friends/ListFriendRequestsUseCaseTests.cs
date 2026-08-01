using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Tests.Friends;

public sealed class ListFriendRequestsUseCaseTests
{
    [Fact]
    public async Task Execute_ReturnsExactUsernameOnlyIncomingAndOutgoingShapes_D068()
    {
        UserId viewer = UserId.New();
        Instant now = FriendTestData.Clock().GetCurrentInstant();
        var repository = new FriendTestFriendshipRepository
        {
            Pending =
            [
                new PendingFriendProjection(
                    FriendshipId.New(), FriendTestData.Active("incoming", "Hidden One"), UserId.New(), now),
                new PendingFriendProjection(
                    FriendshipId.New(), FriendTestData.Active("outgoing", "Hidden Two"), viewer, now - Duration.FromMinutes(1)),
            ],
        };

        FriendRequestList result = await new ListFriendRequestsUseCase(repository)
            .ExecuteAsync(viewer, default);

        Assert.Equal("incoming", Assert.Single(result.Incoming).Username);
        Assert.Equal("outgoing", Assert.Single(result.Outgoing).Username);
        Assert.Equal(
            ["FriendshipId", "RequestedAt", "Username"],
            typeof(FriendRequestItem).GetProperties().Select(property => property.Name).Order().ToArray());
    }
}
