using NodaTime;
using PartyUp.Application.Onboarding;
using PartyUp.Application.Profile;
using PartyUp.Application.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Profile;

public sealed class AvatarCatalogTests
{
    private const string OffCatalogGlyph = "\U0001F346";

    [Fact]
    public void Catalog_HoldsTwentyFourGlyphs_D050()
    {
        Assert.Equal(24, AvatarCatalog.Glyphs.Length);
        Assert.Equal(AvatarCatalog.Glyphs.Length, AvatarCatalog.Glyphs.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("\U0001F3AE", AvatarCatalog.Glyphs[0]);
        Assert.Equal("\U0001F579\uFE0F", AvatarCatalog.Glyphs[2]);
        Assert.Equal("\u265F\uFE0F", AvatarCatalog.Glyphs[5]);
        Assert.False(AvatarCatalog.Contains(OffCatalogGlyph));
    }

    [Fact]
    public void Validate_AcceptsEveryCatalogGlyph_D050()
    {
        var validator = new ValidateProfileDraftUseCase(new ProfanityFilter(), DateTimeZoneProviders.Tzdb);

        foreach (string glyph in AvatarCatalog.Glyphs)
        {
            ProfileDraftValidation validation = validator.Execute(
                new ProfileDraft("player", "Player", new AvatarDraft("emoji", glyph, null), "America/Fortaleza", "en"));

            Assert.True(validation.IsValid, $"catalog glyph {glyph} was rejected.");
        }
    }

    [Fact]
    public void Validate_RejectsAnOffCatalogGlyph_D050()
    {
        var validator = new ValidateProfileDraftUseCase(new ProfanityFilter(), DateTimeZoneProviders.Tzdb);

        ProfileDraftValidation validation = validator.Execute(
            new ProfileDraft("player", "Player", new AvatarDraft("emoji", OffCatalogGlyph, null), "America/Fortaleza", "en"));

        Assert.Equal(["errors.avatar.invalid"], validation.Errors["avatar"]);
    }

    [Fact]
    public async Task ChangeAvatar_RejectsAnOffCatalogGlyph_D050()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 8, 11, 12, 0));
        User user = ActiveUser(clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var useCase = new ChangeAvatarUseCase(users, new FakeUnitOfWork());

        ProfileMutationResult result = await useCase.ExecuteAsync(
            user.Id,
            new AvatarDraft("emoji", OffCatalogGlyph, null),
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("errors.avatar.invalid", result.Code);
        Assert.Equal("\U0001F642", user.Avatar!.Value.Emoji);
    }

    [Fact]
    public void Validate_RejectsAPlatformKindDraft_D050()
    {
        var validator = new ValidateProfileDraftUseCase(new ProfanityFilter(), DateTimeZoneProviders.Tzdb);

        ProfileDraftValidation validation = validator.Execute(
            new ProfileDraft(
                "player",
                "Player",
                new AvatarDraft("platform", null, Guid.NewGuid()),
                "America/Fortaleza",
                "en"));

        Assert.Equal(["errors.avatar.invalid"], validation.Errors["avatar"]);
    }

    [Fact]
    public async Task ChangeAvatar_RejectsAPlatformKindDraft_D050()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 8, 11, 12, 0));
        User user = ActiveUser(clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var useCase = new ChangeAvatarUseCase(users, new FakeUnitOfWork());

        ProfileMutationResult result = await useCase.ExecuteAsync(
            user.Id,
            new AvatarDraft("platform", null, Guid.NewGuid()),
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("errors.avatar.invalid", result.Code);
        Assert.Equal("\U0001F642", user.Avatar!.Value.Emoji);
    }

    [Fact]
    public async Task ChangeAvatar_AcceptsACatalogGlyph_D050()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 8, 11, 12, 0));
        User user = ActiveUser(clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var useCase = new ChangeAvatarUseCase(users, new FakeUnitOfWork());

        ProfileMutationResult result = await useCase.ExecuteAsync(
            user.Id,
            new AvatarDraft("emoji", AvatarCatalog.Glyphs[3], null),
            default);

        Assert.True(result.Succeeded);
        Assert.Equal(AvatarCatalog.Glyphs[3], user.Avatar!.Value.Emoji);
    }

    /// <summary>AVATARS_DESIGN.md §3.5: a source draft naming the caller's own avatar source is accepted.</summary>
    [Fact]
    public async Task ChangeAvatar_AcceptsTheCallersOwnSource_D190()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 8, 11, 12, 0));
        User user = ActiveUser(clock);
        AvatarSource source = user.CaptureAvatarSource(
            AvatarSourceKind.SteamPlatform, [1, 2, 3], "image/webp", clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var useCase = new ChangeAvatarUseCase(users, new FakeUnitOfWork());

        ProfileMutationResult result = await useCase.ExecuteAsync(
            user.Id,
            new AvatarDraft("source", null, null, source.Id.Value),
            default);

        Assert.True(result.Succeeded);
        Assert.True(user.Avatar!.Value.IsExternal);
        Assert.Equal(source.Id, user.Avatar.Value.SourceId);
        // AVATARS_DESIGN.md §3.1/§3.3: the token is denormalized from the AvatarSource, not
        // whatever (nothing, here) the draft supplied.
        Assert.Equal(source.Token, user.Avatar.Value.Token);
    }

    /// <summary>
    /// AVATARS_DESIGN.md §3.5/D-050: a source draft naming another user's avatar source (or one
    /// that no longer exists) fails ownership and must not escape as an unmapped DomainException.
    /// </summary>
    [Fact]
    public async Task ChangeAvatar_RejectsAForeignSource_D050()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 8, 11, 12, 0));
        User user = ActiveUser(clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var useCase = new ChangeAvatarUseCase(users, new FakeUnitOfWork());

        ProfileMutationResult result = await useCase.ExecuteAsync(
            user.Id,
            new AvatarDraft("source", null, null, Guid.NewGuid()),
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("errors.avatar.invalid", result.Code);
        Assert.Equal("\U0001F642", user.Avatar!.Value.Emoji);
    }

    /// <summary>AVATARS_DESIGN.md §3.5: a source draft with no id is rejected the same way.</summary>
    [Fact]
    public void Validate_RejectsASourceDraftWithNoId_D050()
    {
        var validator = new ValidateProfileDraftUseCase(new ProfanityFilter(), DateTimeZoneProviders.Tzdb);

        ProfileDraftValidation validation = validator.Execute(
            new ProfileDraft("player", "Player", new AvatarDraft("source", null, null, null), "America/Fortaleza", "en"));

        Assert.Equal(["errors.avatar.invalid"], validation.Errors["avatar"]);
    }

    private static User ActiveUser(IClock clock)
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            clock);
        user.CompleteOnboarding(
            Username.Create("player"),
            DisplayName.Create("Player"),
            Avatar.FromEmoji("\U0001F642"),
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"),
            clock);
        return user;
    }
}
