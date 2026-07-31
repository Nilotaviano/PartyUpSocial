using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

public sealed class FriendCodeFormatTests
{
    [Fact]
    public void IsWellFormed_RejectsAmbiguousSymbols_D144()
    {
        Assert.True(FriendCodeFormat.IsWellFormed("23456789AB"));
        Assert.False(FriendCodeFormat.IsWellFormed("03456789AB"));
        Assert.False(FriendCodeFormat.IsWellFormed("O3456789AB"));
        Assert.False(FriendCodeFormat.IsWellFormed("I3456789AB"));
        Assert.False(FriendCodeFormat.IsWellFormed("13456789AB"));
    }
}
