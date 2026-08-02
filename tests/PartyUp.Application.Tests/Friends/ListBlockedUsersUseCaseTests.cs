using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Tests.Friends;

public sealed class ListBlockedUsersUseCaseTests
{
    [Fact]
    public async Task Execute_ReturnsStoredUsernameSnapshotsForViewerOwnedRows_D065()
    {
        UserId viewer = UserId.New();
        var repository = new FriendTestBlockRepository
        {
            Outbound =
            [
                new BlockedUserProjection(
                    BlockId.New(), "blocked-user", FriendTestData.Clock().GetCurrentInstant()),
            ],
        };

        BlockedUserList result = await new ListBlockedUsersUseCase(repository)
            .ExecuteAsync(viewer, default);

        BlockedUserItem item = Assert.Single(result.Items);
        Assert.Equal("blocked-user", item.Username);
        Assert.Equal(
            ["BlockId", "CreatedAt", "Username"],
            typeof(BlockedUserItem).GetProperties().Select(property => property.Name).Order().ToArray());
    }
}
