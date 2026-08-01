using PartyUp.Application.Friends;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Tests.Friends;

public sealed class ResolveFriendCodeUseCaseTests
{
    [Fact]
    public async Task Execute_NormalizesSpacesDashesCaseAndResolves_D144()
    {
        var resolver = new FriendTestResolver
        {
            Result = FriendTestData.Resolved(
                FriendTestData.Active("alex", "Alex"),
                ProfileDisclosure.UsernameOnly),
        };

        ResolveFriendCodeResult result = await new ResolveFriendCodeUseCase(resolver)
            .ExecuteAsync(UserId.New(), " 23-45 67-89-ab ", default);

        Assert.IsType<FriendCodeFound>(result);
        Assert.Equal("23456789AB", resolver.LastFriendCode);
    }

    [Fact]
    public async Task Execute_ReturnsHiddenForABlockedCounterpart_D065()
    {
        ResolveFriendCodeResult result = await new ResolveFriendCodeUseCase(new FriendTestResolver())
            .ExecuteAsync(UserId.New(), "23456789AB", default);

        Assert.IsType<FriendCodeHidden>(result);
    }

    [Fact]
    public async Task Execute_ReturnsNoCounterpartCodeOrShareUrl_D068()
    {
        var resolver = new FriendTestResolver
        {
            Result = FriendTestData.Resolved(
                FriendTestData.Active("alex", "Alex"),
                ProfileDisclosure.UsernameOnly),
        };

        FriendCodeFound found = Assert.IsType<FriendCodeFound>(
            await new ResolveFriendCodeUseCase(resolver)
                .ExecuteAsync(UserId.New(), "23456789AB", default));

        Assert.Equal(["Profile"], found.Resolution.GetType().GetProperties().Select(property => property.Name));
        Assert.IsType<UsernameOnlyProfile>(found.Resolution.Profile);
    }
}
