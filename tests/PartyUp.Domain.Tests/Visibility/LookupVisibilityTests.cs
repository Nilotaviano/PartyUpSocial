using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Tests.Users;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Visibility;

public class LookupVisibilityTests
{
    [Fact]
    public void IsListed_KeepsAnyActiveUser_D068()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.True(LookupVisibility.IsListed(TestUsers.Active(clock, "alex")));
    }

    [Fact]
    public void IsListed_ExcludesAnOnboardingAccount_D127()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.False(LookupVisibility.IsListed(TestUsers.Onboarding(clock)));
    }

    [Fact]
    public void IsListed_ExcludesATombstone_D092()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.False(LookupVisibility.IsListed(TestUsers.Tombstoned(clock, "alex")));
    }

    [Fact]
    public void IsListed_KeepsAUserInTheDeletionGracePeriod_D148()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.True(LookupVisibility.IsListed(TestUsers.PendingDeletion(clock, "alex")));
    }

    [Fact]
    public void CanSurface_ExcludesABlockedCounterpart_D065()
    {
        FakeClock clock = TestUsers.Clock();
        User candidate = TestUsers.Active(clock, "alex");

        Assert.False(LookupVisibility.CanSurface(candidate, TestViewers.Of(UserId.New(), blocked: [candidate.Id])));
        Assert.True(LookupVisibility.CanSurface(candidate, TestViewers.Stranger()));
    }

    [Fact]
    public void CanReceiveFriendRequest_RefusesAnOnboardingAccount_D127()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.False(LookupVisibility.CanReceiveFriendRequest(TestUsers.Onboarding(clock), TestViewers.Stranger()));
    }

    [Fact]
    public void CanReceiveFriendRequest_RefusesATombstone_D092()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.False(LookupVisibility.CanReceiveFriendRequest(TestUsers.Tombstoned(clock, "alex"), TestViewers.Stranger()));
    }

    [Fact]
    public void CanReceiveFriendRequest_RefusesTheViewerThemselves_D015()
    {
        FakeClock clock = TestUsers.Clock();
        User candidate = TestUsers.Active(clock, "alex");

        Assert.False(LookupVisibility.CanReceiveFriendRequest(candidate, TestViewers.Of(candidate.Id)));
    }

    [Fact]
    public void CanReceiveFriendRequest_AcceptsAnUnrelatedActiveUser_D015()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.True(LookupVisibility.CanReceiveFriendRequest(TestUsers.Active(clock, "alex"), TestViewers.Stranger()));
    }

    [Fact]
    public void CanMatchSteamImport_NeverSurfacesABlockedCounterpart_D049()
    {
        FakeClock clock = TestUsers.Clock();
        User candidate = TestUsers.Active(clock, "alex");

        Assert.False(LookupVisibility.CanMatchSteamImport(candidate, TestViewers.Of(UserId.New(), blocked: [candidate.Id])));
    }

    [Fact]
    public void CanMatchSteamImport_NeverSurfacesATombstoneOrAnOnboardingAccount_D049()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.False(LookupVisibility.CanMatchSteamImport(TestUsers.Tombstoned(clock, "alex"), TestViewers.Stranger()));
        Assert.False(LookupVisibility.CanMatchSteamImport(TestUsers.Onboarding(clock), TestViewers.Stranger()));
    }

    [Fact]
    public void CanSurface_RejectsNullArguments_D065()
    {
        FakeClock clock = TestUsers.Clock();
        User candidate = TestUsers.Active(clock);

        Assert.Throws<ArgumentNullException>(() => LookupVisibility.IsListed(null!));
        Assert.Throws<ArgumentNullException>(() => LookupVisibility.CanSurface(null!, TestViewers.Stranger()));
        Assert.Throws<ArgumentNullException>(() => LookupVisibility.CanSurface(candidate, null!));
        Assert.Throws<ArgumentNullException>(() => LookupVisibility.CanReceiveFriendRequest(null!, TestViewers.Stranger()));
        Assert.Throws<ArgumentNullException>(() => LookupVisibility.CanReceiveFriendRequest(candidate, null!));
    }
}
