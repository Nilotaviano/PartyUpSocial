using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Friends;

namespace PartyUp.Api.IntegrationTests.Friends;

public sealed class PublicLinkBuilderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("example.test")]
    [InlineData("ftp://example.test")]
    [InlineData("https://example.test?tenant=one")]
    [InlineData("https://example.test#fragment")]
    public void Constructor_RejectsInvalidPublicBaseUrl(string? publicBaseUrl)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new PublicLinkBuilder(publicBaseUrl!));

        Assert.Equal("publicBaseUrl", exception.ParamName);
    }

    [Fact]
    public void ProfileAndAdd_TrimBaseUrlEscapeUsernameAndKeepCanonicalCode_D144()
    {
        var links = new PublicLinkBuilder("  https://example.test///  ");

        Assert.Equal(
            "https://example.test/u/alex%2Fparty%3F",
            links.Profile(Username.Create("alex/party?")));
        Assert.Equal(
            "https://example.test/add/23456789AB",
            links.Add(FriendCode.Create("23456789AB")));
    }

    [Fact]
    public void Add_RejectsANonCanonicalFriendCode_D144()
    {
        var links = new PublicLinkBuilder("https://example.test");

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => links.Add(FriendCode.Create("O3456789AB")));

        Assert.Equal("friendCode", exception.ParamName);
    }
}
