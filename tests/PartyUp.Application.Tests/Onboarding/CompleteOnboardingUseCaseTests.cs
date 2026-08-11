using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Onboarding;
using PartyUp.Application.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Onboarding;

public sealed class CompleteOnboardingUseCaseTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 31, 12, 0);

    [Fact]
    public async Task Execute_ActivatesTheAccountOnlyWithACompleteProfile_D127()
    {
        TestContext context = Create();

        OnboardingCompletionResult result = await context.UseCase.ExecuteAsync(
            new ProfileDraft(
                "player",
                "Player",
                new AvatarDraft("emoji", "\U0001F3AE", null),
                null,
                "en"),
            default);

        Assert.False(result.Succeeded);
        Assert.True(context.User.IsOnboarding);
        Assert.Empty(context.Usernames.Entries);
    }

    [Fact]
    public async Task Execute_ClaimsTheUsernameCaseInsensitively_D128()
    {
        TestContext context = Create();

        OnboardingCompletionResult result = await context.UseCase.ExecuteAsync(
            CompleteDraft("Player.Name"),
            default);

        Assert.True(result.Succeeded);
        Assert.True(context.User.IsActive);
        Assert.True(context.Usernames.Entries.ContainsKey("player.name"));
    }

    private static TestContext Create()
    {
        var clock = new TestClock(Now);
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var usernames = new FakeUsernameRepository();
        var current = new CurrentUser(
            user.Id,
            UserLifecycle.Onboarding,
            false,
            null,
            false,
            false,
            Locale.Create("en"));
        var validator = new ValidateProfileDraftUseCase(
            new ProfanityFilter(),
            DateTimeZoneProviders.Tzdb);
        var useCase = new CompleteOnboardingUseCase(
            new FakeCurrentUserAccessor(current),
            users,
            usernames,
            validator,
            new FakeUnitOfWork(),
            DateTimeZoneProviders.Tzdb,
            clock);
        return new TestContext(useCase, user, usernames);
    }

    internal static ProfileDraft CompleteDraft(string username) =>
        new(
            username,
            "Player",
            new AvatarDraft("emoji", "\U0001F3AE", null),
            "America/Fortaleza",
            "en");

    private sealed record TestContext(
        CompleteOnboardingUseCase UseCase,
        User User,
        FakeUsernameRepository Usernames);
}

public sealed class SocialOnlyAccountTests
{
    [Fact]
    public async Task Execute_CompletesOnboardingWithNoEmail_D078()
    {
        Instant now = Instant.FromUtc(2026, 7, 31, 12, 0);
        var clock = new TestClock(now);
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("KLMNPQRSTU"),
            AgeGateSource.SelfAttested,
            clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var useCase = new CompleteOnboardingUseCase(
            new FakeCurrentUserAccessor(new CurrentUser(
                user.Id,
                UserLifecycle.Onboarding,
                false,
                null,
                false,
                false,
                null)),
            users,
            new FakeUsernameRepository(),
            new ValidateProfileDraftUseCase(
                new ProfanityFilter(),
                DateTimeZoneProviders.Tzdb),
            new FakeUnitOfWork(),
            DateTimeZoneProviders.Tzdb,
            clock);

        OnboardingCompletionResult result = await useCase.ExecuteAsync(
            CompleteOnboardingUseCaseTests.CompleteDraft("socialplayer"),
            default);

        Assert.True(result.Succeeded);
        Assert.True(user.IsActive);
    }
}
