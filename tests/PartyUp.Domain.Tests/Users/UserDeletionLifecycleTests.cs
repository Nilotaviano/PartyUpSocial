using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

public class UserDeletionLifecycleTests
{
    [Fact]
    public void DeletionGracePeriod_IsSevenDays_D148()
    {
        Assert.Equal(Duration.FromDays(7), User.DeletionGracePeriod);
    }

    [Fact]
    public void RequestDeletion_MovesToPendingDeletionWithASevenDayGrace_D148()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        user.RequestDeletion(clock);

        Assert.Equal(UserLifecycle.PendingDeletion, user.Lifecycle);
        Assert.True(user.IsPendingDeletion);
        Assert.Equal(TestUsers.Origin + Duration.FromDays(7), user.GraceEndsAt);
    }

    [Fact]
    public void RequestDeletion_LeavesTheDomainUntouchedDuringTheGrace_D148()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));

        user.RequestDeletion(clock);

        Assert.Equal("gamer", user.Username?.Value);
        Assert.Equal("Gamer", user.DisplayName?.Value);
        Assert.NotNull(user.FriendCode);
        Assert.Single(user.PlatformLinks);
        Assert.True(user.IsDiscoverable);
        Assert.True(user.CanBeFriended);
        Assert.Null(user.DeletedAt);
    }

    [Fact]
    public void RequestDeletion_RaisesDeletionRequestedCarryingTheGraceEnd_D148()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        user.RequestDeletion(clock);

        UserDeletionRequested raised = Assert.Single(user.DequeueEvents().OfType<UserDeletionRequested>());
        Assert.Equal(user.Id, raised.UserId);
        Assert.Equal(TestUsers.Origin + User.DeletionGracePeriod, raised.GraceEndsAt);
    }

    [Fact]
    public void RequestDeletion_IsRejectedTwice_D148()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.PendingDeletion(clock);

        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.RequestDeletion(clock)).Error);
    }

    [Fact]
    public void RequestDeletion_IsRejectedWhileOnboarding_D148()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);

        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.RequestDeletion(clock)).Error);
    }

    [Fact]
    public void CancelDeletion_RestoresActiveAndClearsTheGrace_D158()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.PendingDeletion(clock);

        clock.Advance(Duration.FromDays(3));
        user.CancelDeletion(clock);

        Assert.Equal(UserLifecycle.Active, user.Lifecycle);
        Assert.True(user.IsActive);
        Assert.Null(user.GraceEndsAt);
        Assert.Equal("gamer", user.Username?.Value);
    }

    [Fact]
    public void CancelDeletion_WorksUntilTheLastTickOfTheGrace_D158()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.PendingDeletion(clock);

        clock.Advance(User.DeletionGracePeriod - Duration.FromNanoseconds(1L));
        user.CancelDeletion(clock);

        Assert.True(user.IsActive);
    }

    [Fact]
    public void CancelDeletion_IsRejectedOnceTheGraceHasElapsed_D158()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.PendingDeletion(clock);

        clock.Advance(User.DeletionGracePeriod);

        DomainException error = Assert.Throws<DomainException>(() => user.CancelDeletion(clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
        Assert.True(user.IsPendingDeletion);
    }

    [Fact]
    public void CancelDeletion_RaisesDeletionCanceled_D158()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.PendingDeletion(clock);

        user.CancelDeletion(clock);

        Assert.Equal(user.Id, Assert.Single(user.DequeueEvents().OfType<UserDeletionCanceled>()).UserId);
    }

    [Fact]
    public void CancelDeletion_IsRejectedForAnActiveUser_D158()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.CancelDeletion(clock)).Error);
    }

    [Fact]
    public void CancelDeletion_ThenRequestAgain_StartsAFreshGrace_D148()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.PendingDeletion(clock);

        clock.Advance(Duration.FromDays(2));
        user.CancelDeletion(clock);
        user.RequestDeletion(clock);

        Assert.Equal(TestUsers.Origin + Duration.FromDays(2) + User.DeletionGracePeriod, user.GraceEndsAt);
    }

    [Fact]
    public void MarkDeleted_IsRejectedBeforeTheGraceElapses_D148()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.PendingDeletion(clock);

        clock.Advance(User.DeletionGracePeriod - Duration.FromNanoseconds(1L));

        DomainException error = Assert.Throws<DomainException>(() => user.MarkDeleted(clock));

        Assert.Equal(DomainError.WindowClosed, error.Error);
        Assert.True(user.IsPendingDeletion);
        Assert.Equal("gamer", user.Username?.Value);
    }

    [Fact]
    public void MarkDeleted_NullsThePiiAndKeepsTheRowIdentityStable_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        UserId id = user.Id;
        user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));
        user.RequestDeletion(clock);

        clock.Advance(User.DeletionGracePeriod);
        user.MarkDeleted(clock);

        Assert.Equal(id, user.Id);
        Assert.Equal(UserLifecycle.Deleted, user.Lifecycle);
        Assert.True(user.IsTombstoned);
        Assert.Null(user.Username);
        Assert.Null(user.DisplayName);
        Assert.Null(user.Avatar);
        Assert.Null(user.FriendCode);
        Assert.Empty(user.PlatformLinks);
        Assert.Null(user.GraceEndsAt);
        Assert.Equal(TestUsers.Origin + User.DeletionGracePeriod, user.DeletedAt);
    }

    [Fact]
    public void MarkDeleted_LeavesTheTombstoneOutOfEveryLookupSurface_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Tombstoned(clock);

        Assert.False(user.IsDiscoverable);
        Assert.False(user.CanBeFriended);
        Assert.False(user.IsProfileComplete);
    }

    [Fact]
    public void MarkDeleted_RaisesTombstoned_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.PendingDeletion(clock);

        clock.Advance(User.DeletionGracePeriod);
        user.MarkDeleted(clock);

        UserTombstoned raised = Assert.Single(user.DequeueEvents().OfType<UserTombstoned>());
        Assert.Equal(user.Id, raised.UserId);
        Assert.Equal(TestUsers.Origin + User.DeletionGracePeriod, raised.DeletedAt);
    }

    [Fact]
    public void MarkDeleted_IsIdempotentSoAReRunSweepIsANoOp_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Tombstoned(clock);
        Instant deletedAt = user.DeletedAt!.Value;

        clock.Advance(Duration.FromDays(400));
        user.MarkDeleted(clock);

        Assert.Equal(deletedAt, user.DeletedAt);
        Assert.Empty(user.DequeueEvents());
    }

    [Fact]
    public void MarkDeleted_IsRejectedForAnActiveUser_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.MarkDeleted(clock)).Error);
    }

    [Fact]
    public void ATombstonedUser_CannotCancelDeletion_D158()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Tombstoned(clock);

        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.CancelDeletion(clock)).Error);
    }

    [Fact]
    public void ATombstone_RejectsEveryProfileEdit_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Tombstoned(clock);

        Assert.Throws<DomainException>(() => user.ChangeDisplayName(DisplayName.Create("Back")));
        Assert.Throws<DomainException>(() => user.ChangeAvatar(Avatar.FromEmoji(TestUsers.Emoji)));
        Assert.Throws<DomainException>(() => user.SetReminderDefault(ReminderOffset.Off));
    }

    [Fact]
    public void Tombstoning_RetiresTheUsernameIntoAnOwnerlessReservation_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        UsernameRegistryEntry entry = UsernameRegistry.Claim(null, user.Username!.Value, user.Id, clock);
        user.RequestDeletion(clock);
        clock.Advance(User.DeletionGracePeriod);

        UsernameRegistry.Release(entry, user.Id, clock);
        user.MarkDeleted(clock);

        Assert.True(entry.IsReserved);
        Assert.Null(entry.OwnerId);
        Assert.Equal("gamer", entry.NameCanonical);
        Assert.Equal(TestUsers.Origin + User.DeletionGracePeriod + UsernameRegistry.ReservationPeriod, entry.ReservedUntil);
        Assert.False(entry.IsClaimable(clock));
    }

    [Fact]
    public void IsGraceElapsed_IsFalseWhenNoDeletionIsPending_D148()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.False(TestUsers.Active(clock).IsGraceElapsed(clock));
    }
}
