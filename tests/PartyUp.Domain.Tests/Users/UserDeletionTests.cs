using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

public sealed class UserDeletionTests
{
    [Fact]
    public void RequestDeletion_IsAllowedFromOnboarding_D072()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);

        user.RequestDeletion(clock);

        Assert.True(user.IsPendingDeletion);
        Assert.Equal(TestUsers.Origin + User.DeletionGracePeriod, user.GraceEndsAt);
    }

    [Fact]
    public void MarkDeleted_TombstonesAnAccountThatNeverOnboarded_D072()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);
        user.RequestDeletion(clock);
        clock.Advance(User.DeletionGracePeriod);

        user.MarkDeleted(clock);

        Assert.True(user.IsTombstoned);
        Assert.Null(user.Username);
        Assert.Null(user.DisplayName);
        Assert.Null(user.Avatar);
        Assert.Null(user.FriendCode);
    }

    [Fact]
    public void CancelDeletion_ReturnsAnOnboardingUserToOnboarding_D158()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);
        user.RequestDeletion(clock);

        user.CancelDeletion(clock);

        Assert.True(user.IsOnboarding);
        Assert.False(user.IsProfileComplete);
        Assert.Null(user.GraceEndsAt);
    }

    [Fact]
    public void CancelDeletion_ReturnsAnActiveUserToActive_D158()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        user.RequestDeletion(clock);

        user.CancelDeletion(clock);

        Assert.True(user.IsActive);
        Assert.True(user.IsProfileComplete);
        Assert.Null(user.GraceEndsAt);
    }

    [Fact]
    public void CancelDeletion_NeverProducesAnActiveUserWithAPartialProfile_D127()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);
        user.RequestDeletion(clock);

        user.CancelDeletion(clock);

        Assert.Equal(UserLifecycle.Onboarding, user.Lifecycle);
        Assert.False(user.IsActive);
        Assert.False(user.IsProfileComplete);
    }
}
