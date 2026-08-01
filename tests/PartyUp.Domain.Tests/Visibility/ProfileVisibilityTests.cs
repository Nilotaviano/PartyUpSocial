using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Tests.Users;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Visibility;

public class ProfileVisibilityTests
{
    [Fact]
    public void For_AuthorizesUsernameOnlyOnProfileBeforeAcceptance_D068()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");

        Assert.Equal(ProfileDisclosure.UsernameOnly, ProfileVisibility.For(subject, TestViewers.Stranger()));
    }

    [Fact]
    public void For_ExposesOnlyTheUsernameBeforeAFriendshipIsAccepted_D068()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");

        Assert.Equal(ProfileDisclosure.UsernameOnly, ProfileVisibility.For(subject, TestViewers.Stranger()));
    }

    [Fact]
    public void For_OpensTheFullProfileToAnAcceptedFriend_D068()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");

        Assert.Equal(ProfileDisclosure.Full, ProfileVisibility.For(subject, TestViewers.FriendOf(subject.Id)));
    }

    [Fact]
    public void For_OpensTheFullProfileToItsOwner_D068()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");

        Assert.Equal(ProfileDisclosure.Full, ProfileVisibility.For(subject, TestViewers.Of(subject.Id)));
    }

    [Fact]
    public void For_GovernsProfilePagesOnlyWhileRostersStillRenderTheDisplayName_D068()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");
        Viewer nonFriendWithAccess = TestViewers.Stranger();

        Assert.Equal(ProfileDisclosure.UsernameOnly, ProfileVisibility.For(subject, nonFriendWithAccess));

        MaskedIdentity onARoster = IdentityMasking.ForUser(subject, nonFriendWithAccess);
        Assert.False(onARoster.IsAnonymous);
        Assert.Equal(DisplayName.Create("Alex"), onARoster.DisplayName);
    }

    [Fact]
    public void For_HidesABlockedCounterpartsProfile_D065()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");

        Assert.Equal(
            ProfileDisclosure.Hidden,
            ProfileVisibility.For(subject, TestViewers.Of(UserId.New(), friends: null, blocked: [subject.Id])));
    }

    [Fact]
    public void For_HidesATombstonedProfile_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Tombstoned(clock, "alex");

        Assert.Equal(ProfileDisclosure.Hidden, ProfileVisibility.For(subject, TestViewers.Stranger()));
        Assert.Equal(ProfileDisclosure.Hidden, ProfileVisibility.For(subject, TestViewers.FriendOf(subject.Id)));
    }

    [Fact]
    public void For_HidesAnOnboardingProfile_D127()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Onboarding(clock);

        Assert.Equal(ProfileDisclosure.Hidden, ProfileVisibility.For(subject, TestViewers.Stranger()));
    }

    [Fact]
    public void For_KeepsAPendingDeletionProfileVisible_D148()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.PendingDeletion(clock, "alex");

        Assert.Equal(ProfileDisclosure.UsernameOnly, ProfileVisibility.For(subject, TestViewers.Stranger()));
    }

    [Fact]
    public void For_KeepsABannedUsersProfileVisibleToAnExistingFriend_D180()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");
        subject.Ban(clock);

        Assert.Equal(ProfileDisclosure.Full, ProfileVisibility.For(subject, TestViewers.FriendOf(subject.Id)));
    }

    [Fact]
    public void For_HidesABannedUserFromANonFriend_D180()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");
        subject.Ban(clock);

        Assert.Equal(ProfileDisclosure.Hidden, ProfileVisibility.For(subject, TestViewers.Stranger()));
    }

    [Fact]
    public void For_KeepsABannedUsersOwnProfileVisibleToThemselves_D180()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");
        subject.Ban(clock);

        Assert.Equal(ProfileDisclosure.Full, ProfileVisibility.For(subject, TestViewers.Of(subject.Id)));
    }

    [Fact]
    public void For_RejectsNullArguments_D068()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock);

        Assert.Throws<ArgumentNullException>(() => ProfileVisibility.For(null!, TestViewers.Stranger()));
        Assert.Throws<ArgumentNullException>(() => ProfileVisibility.For(subject, null!));
    }
}
