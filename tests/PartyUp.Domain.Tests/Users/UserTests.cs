using NodaTime.Testing;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

public sealed class UserTests
{
    [Fact]
    public void CanBeFriended_IsFalseForABannedUser_D180()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        user.Ban(clock);

        Assert.True(user.IsDiscoverable);
        Assert.False(user.CanBeFriended);
    }
}
