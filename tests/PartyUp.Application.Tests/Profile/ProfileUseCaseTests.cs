using NodaTime;
using PartyUp.Application.Onboarding;
using PartyUp.Application.Profile;
using PartyUp.Application.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Profile;

public sealed class ChangeUsernameUseCaseTests
{
    [Fact]
    public async Task Execute_ReservesTheFormerNameForThirtyDays_D145()
    {
        Instant now = Instant.FromUtc(2026, 7, 31, 12, 0);
        var clock = new TestClock(now);
        User user = ActiveUser(clock, "old-name");
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var usernames = new FakeUsernameRepository();
        UsernameRegistryEntry current = UsernameRegistryEntry.ClaimNew(
            Username.Create("old-name"),
            user.Id);
        usernames.Entries[current.NameCanonical] = current;
        var useCase = new ChangeUsernameUseCase(
            users,
            usernames,
            new ProfanityFilter(),
            new FakeUnitOfWork(),
            clock);

        ProfileMutationResult result = await useCase.ExecuteAsync(
            user.Id,
            "new-name",
            default);

        Assert.True(result.Succeeded);
        Assert.True(current.IsReserved);
        Assert.Null(current.OwnerId);
        Assert.Equal(now + Duration.FromDays(30), current.ReservedUntil);
        Assert.Equal("new-name", user.Username!.Value.Value);
    }

    [Fact]
    public async Task Execute_RejectsASecondChangeInsideTheCooldown_D128()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = ActiveUser(clock, "first-name");
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var usernames = new FakeUsernameRepository();
        UsernameRegistryEntry current = UsernameRegistryEntry.ClaimNew(
            Username.Create("first-name"),
            user.Id);
        usernames.Entries[current.NameCanonical] = current;
        var useCase = new ChangeUsernameUseCase(
            users,
            usernames,
            new ProfanityFilter(),
            new FakeUnitOfWork(),
            clock);
        Assert.True((await useCase.ExecuteAsync(
            user.Id,
            "second-name",
            default)).Succeeded);

        ProfileMutationResult result = await useCase.ExecuteAsync(
            user.Id,
            "third-name",
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("username_cooldown", result.Code);
        Assert.Equal("second-name", user.Username!.Value.Value);
    }

    [Fact]
    public async Task Execute_KeepsTheFriendCodeStable_D128()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = ActiveUser(clock, "stable-name");
        FriendCode before = user.FriendCode!.Value;
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var usernames = new FakeUsernameRepository();
        UsernameRegistryEntry current = UsernameRegistryEntry.ClaimNew(
            Username.Create("stable-name"),
            user.Id);
        usernames.Entries[current.NameCanonical] = current;
        var useCase = new ChangeUsernameUseCase(
            users,
            usernames,
            new ProfanityFilter(),
            new FakeUnitOfWork(),
            clock);

        ProfileMutationResult result = await useCase.ExecuteAsync(
            user.Id,
            "changed-name",
            default);

        Assert.True(result.Succeeded);
        Assert.Equal(before, user.FriendCode);
    }

    private static User ActiveUser(IClock clock, string username)
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            clock);
        user.CompleteOnboarding(
            Username.Create(username),
            DisplayName.Create("Player"),
            Avatar.FromEmoji("🙂"),
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"),
            clock);
        return user;
    }
}

public sealed class CheckUsernameAvailabilityUseCaseTests
{
    [Fact]
    public async Task Execute_ReportsAReservedNameAsUnavailable_D145()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        var usernames = new FakeUsernameRepository();
        UsernameRegistryEntry reserved = UsernameRegistryEntry.ClaimNew(
            Username.Create("reserved-name"),
            UserId.New());
        reserved.Reserve(clock);
        usernames.Entries[reserved.NameCanonical] = reserved;
        var useCase = new CheckUsernameAvailabilityUseCase(
            new ProfanityFilter(),
            usernames,
            clock);

        UsernameAvailability result = await useCase.ExecuteAsync(
            "RESERVED-NAME",
            default);

        Assert.False(result.Available);
        Assert.Equal("reserved", result.Reason);
    }
}

public sealed class ChangeLocaleUseCaseTests
{
    [Fact]
    public async Task Execute_OverridesTheDetectedLanguage_D146()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("KLMNPQRSTU"),
            AgeGateSource.SelfAttested,
            clock);
        user.CompleteOnboarding(
            Username.Create("player"),
            DisplayName.Create("Player"),
            Avatar.FromEmoji("🎮"),
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"),
            clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var useCase = new ChangeLocaleUseCase(users, new FakeUnitOfWork());

        ProfileMutationResult result = await useCase.ExecuteAsync(user.Id, "pt-BR", default);

        Assert.True(result.Succeeded);
        Assert.Equal("pt-BR", user.Locale!.Value.Value);
    }
}
