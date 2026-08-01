using PartyUp.Application.Auth;

namespace PartyUp.Application.Tests.Auth;

public sealed class FriendContinuationTests
{
    [Theory]
    [InlineData("/u/Player.One", "/u/player.one")]
    [InlineData("/u/Álvaro_7", "/u/álvaro_7")]
    [InlineData("/add/23456789ab", "/add/23456789AB")]
    [InlineData("/add/2345-6789-AB", "/add/23456789AB")]
    [InlineData("/add/2345 6789 AB", "/add/23456789AB")]
    public void Validate_AcceptsOnlyCanonicalUsernameAndFriendCodeRoutes_D031(
        string candidate,
        string expected)
    {
        Assert.Equal(expected, FriendContinuation.Validate(candidate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/app")]
    [InlineData("https://partyup.social/u/player")]
    [InlineData("//partyup.social/u/player")]
    [InlineData("/u/player/extra")]
    [InlineData("/u/player?tab=friends")]
    [InlineData("/u/player#fragment")]
    [InlineData("/u/player%2Fextra")]
    [InlineData("/u/player\\extra")]
    [InlineData("/u/../player")]
    [InlineData("/u/-player")]
    [InlineData("/add/23456789AB/extra")]
    [InlineData("/add/23456789AI")]
    public void Validate_DropsHostileOrUnrelatedValues_D031(string? candidate)
    {
        Assert.Null(FriendContinuation.Validate(candidate));
    }
}
