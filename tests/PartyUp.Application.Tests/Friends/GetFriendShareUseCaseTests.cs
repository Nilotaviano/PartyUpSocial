using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Friends;

public sealed class GetFriendShareUseCaseTests
{
    [Fact]
    public async Task Execute_BuildsTheStableAddAndCurrentUsernameLinks_D144()
    {
        User user = FriendTestData.Active("Current Name", "Alex");
        var resolver = new FriendTestResolver
        {
            Result = FriendTestData.Resolved(user, PartyUp.Domain.Visibility.ProfileDisclosure.Full),
        };
        var links = new FriendShareLinkBuilder();

        FriendShare? result = await new GetFriendShareUseCase(resolver, links)
            .ExecuteAsync(user.Id, default);

        Assert.Equal("23456789AB", result!.FriendCode);
        Assert.Equal("https://example.test/add/23456789AB", result.AddUrl);
        Assert.Equal("https://example.test/u/Current%20Name", result.ProfileUrl);
    }
}

internal sealed class FriendShareLinkBuilder : IPublicLinkBuilder
{
    public string Profile(Username username) =>
        $"https://example.test/u/{Uri.EscapeDataString(username.Value)}";

    public string Add(FriendCode friendCode) => $"https://example.test/add/{friendCode.Value}";
}
