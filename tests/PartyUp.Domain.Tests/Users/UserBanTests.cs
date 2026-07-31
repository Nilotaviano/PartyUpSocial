using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Users;

public sealed class UserBanTests
{
    [Fact]
    public void Ban_MarksTheUserBannedWithoutChangingTheLifecycle_D180()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        user.Ban(clock);

        Assert.True(user.IsBanned);
        Assert.Equal(TestUsers.Origin, user.BannedAt);
        Assert.Equal(UserLifecycle.Active, user.Lifecycle);
        UserBanned raised = Assert.Single(user.DequeueEvents().OfType<UserBanned>());
        Assert.Equal(user.Id, raised.UserId);
        Assert.Equal(TestUsers.Origin, raised.BannedAt);
    }

    [Fact]
    public void Ban_IsIdempotent_D180()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        user.Ban(clock);
        clock.Advance(Duration.FromDays(1));
        user.Ban(clock);

        Assert.Equal(TestUsers.Origin, user.BannedAt);
        Assert.Single(user.DequeueEvents().OfType<UserBanned>());
    }

    [Fact]
    public void Unban_RestoresLookupListing_D180()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        user.Ban(clock);
        user.DequeueEvents();
        Assert.False(LookupVisibility.IsListed(user));

        user.Unban();

        Assert.False(user.IsBanned);
        Assert.Null(user.BannedAt);
        Assert.True(LookupVisibility.IsListed(user));
        Assert.Equal(user.Id, Assert.Single(user.DequeueEvents().OfType<UserUnbanned>()).UserId);
    }

    [Fact]
    public void MarkDeleted_KeepsTheBanOnTheTombstone_D180()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        user.Ban(clock);
        user.RequestDeletion(clock);
        clock.Advance(User.DeletionGracePeriod);

        user.MarkDeleted(clock);

        Assert.True(user.IsTombstoned);
        Assert.True(user.IsBanned);
        Assert.Equal(TestUsers.Origin, user.BannedAt);
    }

    [Fact]
    public void Ban_RejectsATombstone_D180()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Tombstoned(clock);

        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.Ban(clock)).Error);
    }

    [Fact]
    public void Unban_RejectsATombstone_D180()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        user.Ban(clock);
        user.RequestDeletion(clock);
        clock.Advance(User.DeletionGracePeriod);
        user.MarkDeleted(clock);

        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(user.Unban).Error);
    }
}
