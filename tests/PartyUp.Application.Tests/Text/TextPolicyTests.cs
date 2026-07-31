using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Onboarding;
using PartyUp.Application.Profile;
using PartyUp.Application.Text;
using PartyUp.Application.Tests.Onboarding;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Text;

public sealed class TextPolicyTests
{
    [Theory]
    [InlineData("fuuuck")]
    [InlineData("sh1tter")]
    [InlineData("mérda")]
    [InlineData("j0der")]
    public void Check_RejectsNormalizedProfanityInEveryLocale_D096(string value)
    {
        var policy = new ProfanityFilter();

        TextPolicyResult result = policy.Check(value, TextField.Username, Locale.Create("en"));

        Assert.False(result.IsAllowed);
        Assert.Equal("errors.text.profanity", result.ErrorKey);
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData(".leading")]
    [InlineData("trailing-")]
    [InlineData("three___underscores")]
    public void Check_RejectsPunctuationOutsideTheFieldAllowList_D096(string value)
    {
        var policy = new ProfanityFilter();

        TextPolicyResult result = policy.Check(value, TextField.Username, null);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Check_RunsBeforeTheUsernameIsClaimed_D096()
    {
        Instant now = Instant.FromUtc(2026, 7, 31, 12, 0);
        var clock = new TestClock(now);
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var usernames = new FakeUsernameRepository();
        var useCase = new CompleteOnboardingUseCase(
            new FakeCurrentUserAccessor(new CurrentUser(
                user.Id,
                UserLifecycle.Onboarding,
                false,
                null,
                false,
                false,
                Locale.Create("en"))),
            users,
            usernames,
            new ValidateProfileDraftUseCase(
                new ProfanityFilter(),
                DateTimeZoneProviders.Tzdb),
            new FakeUnitOfWork(),
            DateTimeZoneProviders.Tzdb,
            clock);

        OnboardingCompletionResult result = await useCase.ExecuteAsync(
            CompleteOnboardingUseCaseTests.CompleteDraft("fuckname"),
            default);

        Assert.False(result.Succeeded);
        Assert.Equal(0, usernames.FindCalls);
    }

    [Fact]
    public async Task Check_RunsBeforeAGamingHandleReachesTheDomain_D096()
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0)));
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var useCase = new LinkPlatformUseCase(
            users,
            new FakeUnitOfWork(),
            new ProfanityFilter());

        PlatformLinkMutationResult linkResult = await useCase.ExecuteAsync(
            new LinkPlatformCommand(user.Id, Platform.Xbox, "sh1tter"),
            default);

        Assert.False(linkResult.Succeeded);
        Assert.Equal("errors.text.profanity", linkResult.Code);

        PlatformLink link = user.LinkPlatform(Platform.Xbox, GamingHandle.Create("OriginalHandle"));
        var changeUseCase = new ChangePlatformHandleUseCase(
            users,
            new FakeUnitOfWork(),
            new ProfanityFilter());
        PlatformLinkMutationResult changeResult = await changeUseCase.ExecuteAsync(
            new ChangePlatformHandleCommand(user.Id, link.Id, "sh1tter"),
            default);

        Assert.False(changeResult.Succeeded);
        Assert.Equal("errors.text.profanity", changeResult.Code);
        Assert.Equal("OriginalHandle", link.Handle.Value);
        Assert.Single(user.PlatformLinks);
    }
}
