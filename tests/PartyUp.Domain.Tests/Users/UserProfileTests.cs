using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

public class UserProfileTests
{
    [Fact]
    public void ReminderDefault_IsOneHourUntilTheUserChangesIt_D082()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.Equal(ReminderOffset.Hours1, TestUsers.Active(clock).ReminderDefault);
        Assert.Equal(ReminderOffset.Hours1, ReminderOffsetExtensions.Default);
    }

    [Theory]
    [InlineData(ReminderOffset.Off)]
    [InlineData(ReminderOffset.Min15)]
    [InlineData(ReminderOffset.Min30)]
    [InlineData(ReminderOffset.Hours1)]
    [InlineData(ReminderOffset.Hours3)]
    [InlineData(ReminderOffset.Hours12)]
    [InlineData(ReminderOffset.Hours24)]
    public void SetReminderDefault_AcceptsEveryOfferedOffset_D082(ReminderOffset offset)
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        user.SetReminderDefault(offset);

        Assert.Equal(offset, user.ReminderDefault);
    }

    [Fact]
    public void SetReminderDefault_RejectsAnUndefinedOffset_D082()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        DomainException error = Assert.Throws<DomainException>(() => user.SetReminderDefault((ReminderOffset)99));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
        Assert.Equal(ReminderOffset.Hours1, user.ReminderDefault);
    }

    [Fact]
    public void SetReminderDefault_IsAvailableWhileOnboarding_D082()
    {
        User user = TestUsers.Onboarding(TestUsers.Clock());

        user.SetReminderDefault(ReminderOffset.Off);

        Assert.Equal(ReminderOffset.Off, user.ReminderDefault);
    }

    [Fact]
    public void EmailNotificationsEnabled_IsTrueUntilTheUserChangesIt_D189()
    {
        FakeClock clock = TestUsers.Clock();

        Assert.True(TestUsers.Active(clock).EmailNotificationsEnabled);
    }

    [Fact]
    public void SetEmailNotifications_DisablesAndReenables_D189()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        user.SetEmailNotifications(false);
        Assert.False(user.EmailNotificationsEnabled);

        user.SetEmailNotifications(true);
        Assert.True(user.EmailNotificationsEnabled);
    }

    [Fact]
    public void SetEmailNotifications_IsAvailableWhileOnboarding_D189()
    {
        User user = TestUsers.Onboarding(TestUsers.Clock());

        user.SetEmailNotifications(false);

        Assert.False(user.EmailNotificationsEnabled);
    }

    [Fact]
    public void LinkPlatform_RecordsThePlatformAndHandle_D032()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        PlatformLink link = user.LinkPlatform(Platform.Xbox, GamingHandle.Create("  xboxer  "));

        Assert.Same(link, Assert.Single(user.PlatformLinks));
        Assert.Equal(Platform.Xbox, link.Platform);
        Assert.Equal("xboxer", link.Handle.Value);
        Assert.Equal(user.Id, link.UserId);
    }

    [Fact]
    public void LinkPlatform_RejectsTheNotApplicablePlatform_D032()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        DomainException error = Assert.Throws<DomainException>(() =>
            user.LinkPlatform(Platform.NotApplicable, GamingHandle.Create("nobody")));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
        Assert.Empty(user.PlatformLinks);
    }

    [Fact]
    public void LinkPlatform_RejectsASecondLinkForTheSamePlatform_D032()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));

        DomainException error = Assert.Throws<DomainException>(() =>
            user.LinkPlatform(Platform.Steam, GamingHandle.Create("other")));

        Assert.Equal(DomainError.Duplicate, error.Error);
        Assert.Single(user.PlatformLinks);
    }

    [Fact]
    public void LinkPlatform_RejectsAnOversizedHandle_D032()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        Assert.Throws<DomainException>(() =>
            user.LinkPlatform(Platform.Steam, GamingHandle.Create(new string('a', 65))));
    }

    [Fact]
    public void LinkPlatform_IsRejectedForATombstone_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Tombstoned(clock);

        DomainException error = Assert.Throws<DomainException>(() =>
            user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben")));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }

    [Fact]
    public void ChangePlatformHandle_UpdatesTheExistingLink_D032()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        PlatformLink link = user.LinkPlatform(Platform.PlayStation, GamingHandle.Create("psn-one"));

        user.ChangePlatformHandle(link.Id, GamingHandle.Create("psn-two"));

        Assert.Equal("psn-two", link.Handle.Value);
    }

    [Fact]
    public void ChangePlatformHandle_RejectsAnUnknownLink_D032()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        DomainException error = Assert.Throws<DomainException>(() =>
            user.ChangePlatformHandle(PlatformLinkId.New(), GamingHandle.Create("nope")));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void ChangeAvatar_ToAPlatformRequiresOneOfTheUsersOwnLinks_D050()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        DomainException error = Assert.Throws<DomainException>(() =>
            user.ChangeAvatar(Avatar.FromPlatformLink(PlatformLinkId.New())));

        Assert.Equal(DomainError.NotFound, error.Error);
        Assert.Equal(AvatarKind.Emoji, user.Avatar?.Kind);
    }

    [Fact]
    public void ChangeAvatar_PointsAtExactlyOneAvatarSource_D050()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        PlatformLink steam = user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));
        PlatformLink xbox = user.LinkPlatform(Platform.Xbox, GamingHandle.Create("xboxer"));

        user.ChangeAvatar(Avatar.FromPlatformLink(steam.Id));

        Assert.True(user.Avatar?.DrawsFrom(steam.Id));
        Assert.False(user.Avatar?.DrawsFrom(xbox.Id));
        Assert.Equal(string.Empty, user.Avatar?.Emoji);
    }

    [Fact]
    public void UnlinkPlatform_FallsTheAvatarBackToAnEmoji_D050()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        PlatformLink link = user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));
        user.ChangeAvatar(Avatar.FromPlatformLink(link.Id));

        user.UnlinkPlatform(link.Id);

        Assert.Empty(user.PlatformLinks);
        Assert.Equal(AvatarKind.Emoji, user.Avatar?.Kind);
        Assert.Equal(Avatar.FallbackEmoji, user.Avatar?.Emoji);
        Assert.True(user.IsProfileComplete);
    }

    [Fact]
    public void UnlinkPlatform_FallsBackToTheEmojiTheUserPicked_D050()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        PlatformLink link = user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));
        user.ChangeAvatar(Avatar.FromPlatformLink(link.Id));

        user.UnlinkPlatform(link.Id, TestUsers.Emoji);

        Assert.Equal(TestUsers.Emoji, user.Avatar?.Emoji);
    }

    [Fact]
    public void UnlinkPlatform_LeavesAnAvatarSourcedFromAnotherLinkAlone_D050()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        PlatformLink steam = user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));
        PlatformLink xbox = user.LinkPlatform(Platform.Xbox, GamingHandle.Create("xboxer"));
        user.ChangeAvatar(Avatar.FromPlatformLink(steam.Id));

        user.UnlinkPlatform(xbox.Id);

        Assert.True(user.Avatar?.DrawsFrom(steam.Id));
        Assert.Single(user.PlatformLinks);
    }

    [Fact]
    public void UnlinkPlatform_LeavesAnEmojiAvatarAlone_D050()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        PlatformLink link = user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));

        user.UnlinkPlatform(link.Id);

        Assert.Equal(TestUsers.Emoji, user.Avatar?.Emoji);
    }

    [Fact]
    public void UnlinkPlatform_RejectsAnUnknownLink_D050()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        DomainException error = Assert.Throws<DomainException>(() => user.UnlinkPlatform(PlatformLinkId.New()));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void ChangeDisplayName_ReplacesTheName_D032()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        user.ChangeDisplayName(DisplayName.Create("  New Name  "));

        Assert.Equal("New Name", user.DisplayName?.Value);
    }

    [Fact]
    public void ChangeDisplayName_RejectsAnUnsetName_D032()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        Assert.Throws<DomainException>(() => user.ChangeDisplayName(default));
    }

    [Fact]
    public void ProfileEdits_AreRejectedWhileOnboarding_D127()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);

        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.ChangeDisplayName(DisplayName.Create("x"))).Error);
        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.ChangeAvatar(Avatar.FromEmoji(TestUsers.Emoji))).Error);
        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.ChangeTimeZone(TestUsers.Zone)).Error);
        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => user.ChangeLocale(Locale.Create("es"))).Error);
    }

    [Fact]
    public void ChangeTimeZone_AndLocale_ReplaceTheStoredValues_D061()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        DateTimeZone lisbon = DateTimeZoneProviders.Tzdb["Europe/Lisbon"];

        user.ChangeTimeZone(lisbon);
        user.ChangeLocale(Locale.Create("es"));

        Assert.Equal(lisbon, user.TimeZone);
        Assert.Equal("es", user.Locale?.Value);
    }

    [Fact]
    public void ChangeTimeZone_RejectsNull_D061()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        Assert.Throws<ArgumentNullException>(() => user.ChangeTimeZone(null!));
    }
}
