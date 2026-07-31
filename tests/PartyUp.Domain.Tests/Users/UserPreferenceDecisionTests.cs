using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

/// <summary>
/// The account preferences the domain owns: the user's own time zone (D-046) and the locale every
/// rendered notification is written in (D-060). Which zone or tag a viewer sees rendered is a
/// presentation concern above this layer.
/// </summary>
public class UserPreferenceDecisionTests
{
    [Fact]
    public void CompleteOnboarding_RequiresTheUsersOwnTimeZone_D046()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Onboarding(clock);

        Assert.Throws<ArgumentNullException>(
            () => user.CompleteOnboarding(
                Username.Create("gamer"),
                DisplayName.Create("Gamer"),
                Avatar.FromEmoji(TestUsers.Emoji),
                null!,
                Locale.Create("en"),
                clock));
        Assert.True(user.IsOnboarding);
    }

    [Fact]
    public void ChangeTimeZone_ReplacesTheSpecifiedZone_D046()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        DateTimeZone lisbon = DateTimeZoneProviders.Tzdb["Europe/Lisbon"];

        Assert.Equal(TestUsers.Zone, user.TimeZone);

        user.ChangeTimeZone(lisbon);

        Assert.Equal(lisbon, user.TimeZone);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    [InlineData("es")]
    public void Locale_CarriesEachLaunchLanguageTag_D060(string tag)
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        user.ChangeLocale(Locale.Create(tag));

        Assert.Equal(tag, user.Locale!.Value.Value);
    }

    [Fact]
    public void Locale_RejectsAnEmptyTag_D060()
    {
        DomainException error = Assert.Throws<DomainException>(() => Locale.Create(" "));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }
}
