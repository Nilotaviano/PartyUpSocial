using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

internal static class TestUsers
{
    internal const string Emoji = "\U0001F642";

    internal static readonly Instant Origin = Instant.FromUtc(2026, 1, 5, 12, 0);

    internal static DateTimeZone Zone => DateTimeZoneProviders.Tzdb["America/Sao_Paulo"];

    internal static FakeClock Clock() => new(Origin);

    internal static FakeClock ClockAt(Instant instant) => new(instant);

    internal static User Onboarding(IClock clock, string friendCode = "PU-ABC123") =>
        User.BeginOnboarding(UserId.New(), FriendCode.Create(friendCode), AgeGateSource.SelfAttested, clock);

    internal static User Active(
        IClock clock,
        string username = "gamer",
        string displayName = "Gamer",
        string friendCode = "PU-ABC123")
    {
        User user = Onboarding(clock, friendCode);
        user.CompleteOnboarding(
            Username.Create(username),
            DisplayName.Create(displayName),
            Avatar.FromEmoji(Emoji),
            Zone,
            Locale.Create("en"),
            clock);
        user.DequeueEvents();
        return user;
    }

    internal static User PendingDeletion(IClock clock, string username = "gamer")
    {
        User user = Active(clock, username);
        user.RequestDeletion(clock);
        user.DequeueEvents();
        return user;
    }

    internal static User Tombstoned(FakeClock clock, string username = "gamer")
    {
        User user = PendingDeletion(clock, username);
        clock.Advance(User.DeletionGracePeriod);
        user.MarkDeleted(clock);
        user.DequeueEvents();
        return user;
    }
}
