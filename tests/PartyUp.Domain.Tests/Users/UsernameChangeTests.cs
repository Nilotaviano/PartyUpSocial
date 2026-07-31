using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

public class UsernameChangeTests
{
    [Fact]
    public void UsernameChangeCooldown_IsThirtyDays_D128()
    {
        Assert.Equal(Duration.FromDays(30), User.UsernameChangeCooldown);
    }

    [Fact]
    public void ChangeUsername_IsAllowedImmediatelyAfterOnboarding_D128()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        Assert.True(user.CanChangeUsername(clock));

        user.ChangeUsername(Username.Create("newname"), clock);

        Assert.Equal("newname", user.Username?.Value);
        Assert.Equal(TestUsers.Origin, user.UsernameChangedAt);
    }

    [Fact]
    public void ChangeUsername_IsRejectedInsideTheThirtyDayCooldown_D128()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        user.ChangeUsername(Username.Create("newname"), clock);

        clock.Advance(Duration.FromDays(29) + Duration.FromHours(23));

        Assert.False(user.CanChangeUsername(clock));
        DomainException error = Assert.Throws<DomainException>(() =>
            user.ChangeUsername(Username.Create("thirdname"), clock));

        Assert.Equal(DomainError.CooldownActive, error.Error);
        Assert.Equal("newname", user.Username?.Value);
    }

    [Fact]
    public void ChangeUsername_IsAllowedAtExactlyThirtyDays_D128()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        user.ChangeUsername(Username.Create("newname"), clock);

        clock.Advance(User.UsernameChangeCooldown);

        Assert.True(user.CanChangeUsername(clock));

        user.ChangeUsername(Username.Create("thirdname"), clock);

        Assert.Equal("thirdname", user.Username?.Value);
        Assert.Equal(TestUsers.Origin + User.UsernameChangeCooldown, user.UsernameChangedAt);
    }

    [Fact]
    public void ChangeUsername_OneTickBeforeThirtyDays_IsStillRejected_D128()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        user.ChangeUsername(Username.Create("newname"), clock);

        clock.Advance(User.UsernameChangeCooldown - Duration.FromNanoseconds(1L));

        Assert.False(user.CanChangeUsername(clock));
        Assert.Throws<DomainException>(() => user.ChangeUsername(Username.Create("thirdname"), clock));
    }

    [Fact]
    public void ChangeUsername_ToADifferentCasingOfTheSameNameSkipsTheCooldown_D128()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        user.ChangeUsername(Username.Create("newname"), clock);

        clock.Advance(Duration.FromDays(1));
        user.ChangeUsername(Username.Create("NewName"), clock);

        Assert.Equal("NewName", user.Username?.Value);
        Assert.Equal(TestUsers.Origin, user.UsernameChangedAt);
        Assert.True(UsernameRegistry.SameName(Username.Create("newname"), Username.Create("NewName")));
    }

    [Fact]
    public void ChangeUsername_KeepsTheFriendCodeStable_D128()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        FriendCode? before = user.FriendCode;

        user.ChangeUsername(Username.Create("newname"), clock);

        Assert.Equal(before, user.FriendCode);
    }

    [Fact]
    public void ChangeUsername_RaisesUsernameChanged_D128()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        user.ChangeUsername(Username.Create("newname"), clock);

        UserUsernameChanged raised = Assert.Single(user.DequeueEvents().OfType<UserUsernameChanged>());
        Assert.Equal(user.Id, raised.UserId);
    }

    [Fact]
    public void ChangeUsername_RejectsAnUnsetName_D128()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        Assert.Equal(
            DomainError.ValidationFailed,
            Assert.Throws<DomainException>(() => user.ChangeUsername(default, clock)).Error);
    }

    [Fact]
    public void ChangeUsername_IsRejectedWhileOnboarding_D127()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);

        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.ChangeUsername(Username.Create("gamer"), clock)).Error);
    }

    [Fact]
    public void ChangeUsername_IsRejectedForATombstone_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Tombstoned(clock);

        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.ChangeUsername(Username.Create("gamer"), clock)).Error);
    }

    [Fact]
    public void CanChangeUsername_RejectsANullClock_D128()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        Assert.Throws<ArgumentNullException>(() => user.CanChangeUsername(null!));
    }
}
