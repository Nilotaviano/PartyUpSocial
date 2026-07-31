using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

public class UserOnboardingTests
{
    [Fact]
    public void BeginOnboarding_LeavesEveryProfileFieldUnset_D127()
    {
        User user = TestUsers.Onboarding(TestUsers.Clock());

        Assert.Equal(UserLifecycle.Onboarding, user.Lifecycle);
        Assert.True(user.IsOnboarding);
        Assert.False(user.IsProfileComplete);
        Assert.Null(user.Username);
        Assert.Null(user.DisplayName);
        Assert.Null(user.Avatar);
        Assert.Null(user.TimeZone);
        Assert.Null(user.Locale);
        Assert.Empty(user.PlatformLinks);
    }

    [Fact]
    public void BeginOnboarding_CarriesTheAgeGateSignalWithoutABirthDate_D093()
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("PU-ABC123"),
            AgeGateSource.Store,
            TestUsers.Clock());

        Assert.True(user.AgeGatePassed);
        Assert.Equal(AgeGateSource.Store, user.AgeGateSource);
    }

    [Fact]
    public void BeginOnboarding_AssignsTheFriendCodeBeforeTheProfileExists_D031()
    {
        User user = TestUsers.Onboarding(TestUsers.Clock(), "pu-abc123");

        Assert.Equal("PU-ABC123", user.FriendCode?.Value);
    }

    [Fact]
    public void BeginOnboarding_StampsCreationFromTheInjectedClock()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.Equal(TestUsers.Origin, TestUsers.Onboarding(clock).CreatedAt);
    }

    [Fact]
    public void BeginOnboarding_DefaultsTheReminderToOneHour_D082()
    {
        Assert.Equal(ReminderOffset.Hours1, TestUsers.Onboarding(TestUsers.Clock()).ReminderDefault);
    }

    [Fact]
    public void BeginOnboarding_RejectsAnUnsetFriendCode_D031()
    {
        DomainException error = Assert.Throws<DomainException>(() =>
            User.BeginOnboarding(UserId.New(), default, AgeGateSource.SelfAttested, TestUsers.Clock()));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void BeginOnboarding_RejectsAnUndefinedAgeGateSource_D093()
    {
        DomainException error = Assert.Throws<DomainException>(() =>
            User.BeginOnboarding(UserId.New(), FriendCode.Create("PU-ABC123"), (AgeGateSource)42, TestUsers.Clock()));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void BeginOnboarding_RejectsANullClock()
    {
        Assert.Throws<ArgumentNullException>(() =>
            User.BeginOnboarding(UserId.New(), FriendCode.Create("PU-ABC123"), AgeGateSource.SelfAttested, null!));
    }

    [Fact]
    public void OnboardingUser_IsInvisibleToLookupAndCannotBeFriended_D127()
    {
        User user = TestUsers.Onboarding(TestUsers.Clock());

        Assert.False(user.IsDiscoverable);
        Assert.False(user.CanBeFriended);
    }

    [Fact]
    public void CompleteOnboarding_SetsEveryProfileFieldAndActivates_D127()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);

        user.CompleteOnboarding(
            Username.Create("gamer"),
            DisplayName.Create("Gamer"),
            Avatar.FromEmoji(TestUsers.Emoji),
            TestUsers.Zone,
            Locale.Create("pt-BR"),
            clock);

        Assert.Equal(UserLifecycle.Active, user.Lifecycle);
        Assert.True(user.IsProfileComplete);
        Assert.True(user.IsDiscoverable);
        Assert.Equal("gamer", user.Username?.Value);
        Assert.Equal("Gamer", user.DisplayName?.Value);
        Assert.Equal(TestUsers.Emoji, user.Avatar?.Emoji);
        Assert.Equal(TestUsers.Zone, user.TimeZone);
        Assert.Equal("pt-BR", user.Locale?.Value);
    }

    [Fact]
    public void CompleteOnboarding_RaisesOnboardingCompleted_D127()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);

        user.CompleteOnboarding(
            Username.Create("gamer"),
            DisplayName.Create("Gamer"),
            Avatar.FromEmoji(TestUsers.Emoji),
            TestUsers.Zone,
            Locale.Create("en"),
            clock);

        UserOnboardingCompleted raised = Assert.Single(user.DequeueEvents().OfType<UserOnboardingCompleted>());
        Assert.Equal(user.Id, raised.UserId);
    }

    [Fact]
    public void CompleteOnboarding_LeavesTheUsernameCooldownUnstarted_D128()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.Null(TestUsers.Active(clock).UsernameChangedAt);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void CompleteOnboarding_RejectsAnyMissingProfileField_D127(
        bool noUsername,
        bool noDisplayName,
        bool noAvatar,
        bool noLocale)
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);

        DomainException error = Assert.Throws<DomainException>(() => user.CompleteOnboarding(
            noUsername ? default : Username.Create("gamer"),
            noDisplayName ? default : DisplayName.Create("Gamer"),
            noAvatar ? default : Avatar.FromEmoji(TestUsers.Emoji),
            TestUsers.Zone,
            noLocale ? default : Locale.Create("en"),
            clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
        Assert.Equal(UserLifecycle.Onboarding, user.Lifecycle);
    }

    [Fact]
    public void CompleteOnboarding_RejectsAMissingTimeZone_D127()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);

        Assert.Throws<ArgumentNullException>(() => user.CompleteOnboarding(
            Username.Create("gamer"),
            DisplayName.Create("Gamer"),
            Avatar.FromEmoji(TestUsers.Emoji),
            null!,
            Locale.Create("en"),
            clock));
    }

    [Fact]
    public void CompleteOnboarding_RejectsASecondCall_D127()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        DomainException error = Assert.Throws<DomainException>(() => user.CompleteOnboarding(
            Username.Create("other"),
            DisplayName.Create("Other"),
            Avatar.FromEmoji(TestUsers.Emoji),
            TestUsers.Zone,
            Locale.Create("en"),
            clock));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
        Assert.Equal("gamer", user.Username?.Value);
    }

    [Fact]
    public void CompleteOnboarding_RejectsAPlatformAvatarWithNoMatchingLink_D050()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);

        DomainException error = Assert.Throws<DomainException>(() => user.CompleteOnboarding(
            Username.Create("gamer"),
            DisplayName.Create("Gamer"),
            Avatar.FromPlatformLink(PlatformLinkId.New()),
            TestUsers.Zone,
            Locale.Create("en"),
            clock));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void CompleteOnboarding_AcceptsAPlatformAvatarLinkedDuringOnboarding_D127()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);
        PlatformLink link = user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));

        user.CompleteOnboarding(
            Username.Create("gamer"),
            DisplayName.Create("Gamer"),
            Avatar.FromPlatformLink(link.Id),
            TestUsers.Zone,
            Locale.Create("en"),
            clock);

        Assert.Equal(AvatarKind.Platform, user.Avatar?.Kind);
        Assert.Equal(link.Id, user.Avatar?.SourcePlatformLinkId);
    }
}
