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
    [InlineData("/guest/0123456789abcdef0123456789abcdef")]
    [InlineData("/guest/ffffffffffffffffffffffffffffffff")]
    public void Validate_AcceptsTheGuestLinkPage_D225(string candidate)
    {
        Assert.Equal(candidate, FriendContinuation.Validate(candidate));
    }

    [Theory]
    [InlineData("/guest/0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("/guest/0123456789abcdef0123456789abcde")]
    [InlineData("/guest/0123456789abcdef0123456789abcdef0")]
    [InlineData("/guest/0123456789abcdef0123456789abcdef/upgrade")]
    [InlineData("/guest/../0123456789abcdef0123456789abcdef")]
    [InlineData("/guest/invalid")]
    [InlineData("https://evil.test/guest/0123456789abcdef0123456789abcdef")]
    [InlineData("//evil.test/guest/0123456789abcdef0123456789abcdef")]
    [InlineData("/guest/0123456789abcdef0123456789abcdef\n")]
    [InlineData("/guest/0123456789abcdef0123456789abcdef\r\n")]
    public void Validate_DropsAnythingButAnExactGuestLinkPage_D225(string candidate)
    {
        Assert.Null(FriendContinuation.Validate(candidate));
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
