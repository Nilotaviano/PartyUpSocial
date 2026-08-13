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
    public void ChangeAvatar_ToExternalRequiresOneOfTheUsersOwnAvatarSources_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        DomainException error = Assert.Throws<DomainException>(() =>
            user.ChangeAvatar(Avatar.FromSource(AvatarSourceId.New())));

        Assert.Equal(DomainError.NotFound, error.Error);
        Assert.Equal(AvatarKind.Emoji, user.Avatar?.Kind);
    }

    [Fact]
    public void ChangeAvatar_PointsAtExactlyOneAvatarSource_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        AvatarSource steam = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [1, 2, 3], "image/webp", clock);
        AvatarSource google = user.CaptureAvatarSource(AvatarSourceKind.GoogleAccount, [4, 5, 6], "image/webp", clock);

        user.ChangeAvatar(Avatar.FromSource(steam.Id));

        Assert.True(user.Avatar?.DrawsFrom(steam.Id));
        Assert.False(user.Avatar?.DrawsFrom(google.Id));
        Assert.Equal(string.Empty, user.Avatar?.Emoji);
    }

    /// <summary>
    /// AVATARS_DESIGN.md §3.1/§3.3: <see cref="Avatar.Token"/> is denormalized from the owning
    /// <see cref="AvatarSource"/> by <see cref="User.ChangeAvatar"/>, never trusted from the caller
    /// -- so a caller passing no token (the only shape <c>ChangeAvatarUseCase</c> ever builds) still
    /// ends up with the real one, and a caller passing a bogus one is overwritten regardless.
    /// </summary>
    [Fact]
    public void ChangeAvatar_DenormalizesTheRealTokenFromTheOwningSource_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        AvatarSource source = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [1, 2, 3], "image/webp", clock);

        user.ChangeAvatar(Avatar.FromSource(source.Id, "not-the-real-token"));

        Assert.Equal(source.Token, user.Avatar?.Token);
        Assert.NotEqual("not-the-real-token", user.Avatar?.Token);
    }

    [Fact]
    public void CaptureAvatarSource_UpsertsTheSingleRowPerKind_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        AvatarSource first = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [1], "image/webp", clock);

        AvatarSource second = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [2], "image/webp", clock);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Token, second.Token);
        Assert.Single(user.AvatarSources);
        Assert.Equal(new byte[] { 2 }, user.AvatarSources[0].Bytes);
    }

    /// <summary>
    /// A-084 revocation: the capability token survives a re-capture on a still-linked account by
    /// design (the refresh job must not churn it), but an unlink deletes the source row, so the
    /// re-link that follows goes through the create branch and mints a fresh token. A token harvested
    /// before an unlink is therefore dead after it.
    /// </summary>
    [Fact]
    public void UnlinkThenRelink_MintsAFreshAvatarProxyToken_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        PlatformLink link = user.LinkPlatform(Platform.Steam, GamingHandle.Create("player-one"));
        AvatarSource before = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [1], "image/webp", clock);
        string harvestedToken = before.Token;

        user.UnlinkPlatform(link.Id);
        Assert.Empty(user.AvatarSources);

        user.LinkPlatform(Platform.Steam, GamingHandle.Create("player-one"));
        AvatarSource after = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [2], "image/webp", clock);

        Assert.NotEqual(harvestedToken, after.Token);
        Assert.NotEqual(before.Id, after.Id);
    }

    [Fact]
    public void RemoveAvatarSource_FallsTheAvatarBackToAnEmoji_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        AvatarSource source = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [1, 2, 3], "image/webp", clock);
        user.ChangeAvatar(Avatar.FromSource(source.Id));

        user.RemoveAvatarSource(source.Id);

        Assert.Empty(user.AvatarSources);
        Assert.Equal(AvatarKind.Emoji, user.Avatar?.Kind);
        Assert.Equal(Avatar.FallbackEmoji, user.Avatar?.Emoji);
        Assert.True(user.IsProfileComplete);
    }

    [Fact]
    public void RemoveAvatarSource_FallsBackToTheEmojiTheUserPicked_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        AvatarSource source = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [1, 2, 3], "image/webp", clock);
        user.ChangeAvatar(Avatar.FromSource(source.Id));

        user.RemoveAvatarSource(source.Id, TestUsers.Emoji);

        Assert.Equal(TestUsers.Emoji, user.Avatar?.Emoji);
    }

    [Fact]
    public void RemoveAvatarSource_LeavesAnAvatarSourcedFromAnotherKindAlone_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        AvatarSource steam = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [1, 2, 3], "image/webp", clock);
        AvatarSource google = user.CaptureAvatarSource(AvatarSourceKind.GoogleAccount, [4, 5, 6], "image/webp", clock);
        user.ChangeAvatar(Avatar.FromSource(steam.Id));

        user.RemoveAvatarSource(google.Id);

        Assert.True(user.Avatar?.DrawsFrom(steam.Id));
        Assert.Single(user.AvatarSources);
    }

    [Fact]
    public void RemoveAvatarSource_RejectsAnUnknownSource_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        DomainException error = Assert.Throws<DomainException>(() =>
            user.RemoveAvatarSource(AvatarSourceId.New()));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void UnlinkPlatform_RemovesTheSteamAvatarSourceAndFallsBackToAnEmoji_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        PlatformLink link = user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));
        AvatarSource source = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [1, 2, 3], "image/webp", clock);
        user.ChangeAvatar(Avatar.FromSource(source.Id));

        user.UnlinkPlatform(link.Id);

        Assert.Empty(user.PlatformLinks);
        Assert.Empty(user.AvatarSources);
        Assert.Equal(AvatarKind.Emoji, user.Avatar?.Kind);
        Assert.Equal(Avatar.FallbackEmoji, user.Avatar?.Emoji);
        Assert.True(user.IsProfileComplete);
    }

    [Fact]
    public void UnlinkPlatform_FallsBackToTheEmojiTheUserPicked_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        PlatformLink link = user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));
        AvatarSource source = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [1, 2, 3], "image/webp", clock);
        user.ChangeAvatar(Avatar.FromSource(source.Id));

        user.UnlinkPlatform(link.Id, TestUsers.Emoji);

        Assert.Equal(TestUsers.Emoji, user.Avatar?.Emoji);
    }

    [Fact]
    public void UnlinkPlatform_LeavesANonSteamPlatformsAvatarSourceAlone_D190()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        PlatformLink steam = user.LinkPlatform(Platform.Steam, GamingHandle.Create("gaben"));
        PlatformLink xbox = user.LinkPlatform(Platform.Xbox, GamingHandle.Create("xboxer"));
        AvatarSource source = user.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, [1, 2, 3], "image/webp", clock);
        user.ChangeAvatar(Avatar.FromSource(source.Id));

        user.UnlinkPlatform(xbox.Id);

        Assert.True(user.Avatar?.DrawsFrom(source.Id));
        Assert.Single(user.PlatformLinks);
        Assert.Single(user.AvatarSources);
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
