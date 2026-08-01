using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Friends;

public sealed class FriendPairLockerContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockedFriendPair_ReturnsCanonicalIdOrderForEitherCallerOrder(bool reverseArguments)
    {
        User lower = Active(UserId.From(Guid.Parse("00000000-0000-0000-0000-000000000001")), "lower");
        User higher = Active(UserId.From(Guid.Parse("00000000-0000-0000-0000-000000000002")), "higher");

        LockedFriendPair pair = reverseArguments
            ? new LockedFriendPair(higher, lower)
            : new LockedFriendPair(lower, higher);

        Assert.Equal(lower.Id, pair.First.Id);
        Assert.Equal(higher.Id, pair.Second.Id);
    }

    private static User Active(UserId id, string username)
    {
        User user = User.BeginOnboarding(
            id,
            FriendCode.Create("23456789AB"),
            AgeGateSource.SelfAttested,
            FriendTestData.Clock());
        user.CompleteOnboarding(
            Username.Create(username),
            DisplayName.Create(username),
            Avatar.Fallback,
            NodaTime.DateTimeZone.Utc,
            Locale.Create("en"),
            FriendTestData.Clock());
        return user;
    }
}
