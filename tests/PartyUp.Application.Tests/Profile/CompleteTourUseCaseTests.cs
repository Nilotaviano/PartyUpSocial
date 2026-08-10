using NodaTime;
using PartyUp.Application.Profile;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Profile;

/// <summary>TOUR_DESIGN.md §1/§4 (D-182).</summary>
public sealed class CompleteTourUseCaseTests
{
    [Fact]
    public async Task Execute_SetsTourCompletedAt_D182()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = ActiveUser(clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var useCase = new CompleteTourUseCase(users, new FakeUnitOfWork(), clock);

        ProfileMutationResult result = await useCase.ExecuteAsync(user.Id, default);

        Assert.True(result.Succeeded);
        Assert.NotNull(user.TourCompletedAt);
    }

    [Fact]
    public async Task Execute_IsIdempotent_FirstInstantWins_D182()
    {
        var firstClock = new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = ActiveUser(firstClock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var firstUseCase = new CompleteTourUseCase(users, new FakeUnitOfWork(), firstClock);

        await firstUseCase.ExecuteAsync(user.Id, default);
        Instant? firstInstant = user.TourCompletedAt;

        var secondClock = new TestClock(Instant.FromUtc(2026, 8, 1, 12, 0));
        var secondUseCase = new CompleteTourUseCase(users, new FakeUnitOfWork(), secondClock);
        await secondUseCase.ExecuteAsync(user.Id, default);

        Assert.Equal(firstInstant, user.TourCompletedAt);
    }

    [Fact]
    public async Task Execute_UnknownUser_Fails_D182()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        var users = new FakeUserRepository();
        var useCase = new CompleteTourUseCase(users, new FakeUnitOfWork(), clock);

        ProfileMutationResult result = await useCase.ExecuteAsync(UserId.New(), default);

        Assert.False(result.Succeeded);
        Assert.Equal("not_found", result.Code);
    }

    private static User ActiveUser(TestClock clock)
    {
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
        return user;
    }
}
