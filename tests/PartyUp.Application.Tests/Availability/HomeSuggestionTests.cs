using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Availability;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §11 "Read stack (S4)": <see cref="GetHomeSuggestionsUseCase"/> takes the identical
/// path as an all-friends overlap search and applies <see cref="OverlapEngine.HomeSuggestions"/> last
/// — never a second ranking (D-116, D-136).
/// </summary>
public sealed class HomeSuggestionTests
{
    private static readonly LocalDate Monday = new(2026, 9, 7);
    private static readonly DateTimeZone Utc = DateTimeZoneProviders.Tzdb["Etc/UTC"];

    [Fact]
    public async Task TopFiveWithSeeAllAndTheFifteenMinuteTwoFriendFloor_D116_D136()
    {
        UserId viewer = UserId.New();
        UserId anchor = UserId.New();
        FakeFriendshipReads friendships = new();
        FakeAvailabilityReads reads = new();
        friendships.Accepted.Add(AcceptedProjection(anchor));

        // Anchor is free the entire day — every overlap the seven partners produce pairs with anchor.
        reads.Owners.Add(OwnerWithBlock(anchor, 0, 24));

        // Seven partners, each free for a disjoint 30-minute slot: seven distinct
        // (anchor, partner) windows of AvailableCount == 2 and Length == 30 minutes — all qualify.
        for (int i = 0; i < 7; i++)
        {
            UserId partner = UserId.New();
            friendships.Accepted.Add(AcceptedProjection(partner));
            int hour = 1 + i;
            reads.Owners.Add(OwnerWithBlockMinutes(partner, hour * 60, hour * 60 + 30));
        }

        GetHomeSuggestionsUseCase useCase = new(new FindOverlapWindowsUseCase(
            friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(BaseInstant())));

        HomeSuggestionsResult result = await useCase.ExecuteAsync(viewer, default);

        Assert.Equal(OverlapEngine.HomeSuggestionLimit, result.Suggestions.Count);
        Assert.True(result.HasMore);
        Assert.All(result.Suggestions, window =>
        {
            Assert.True(window.AvailableCount >= OverlapEngine.MinimumSuggestionAvailable);
            Assert.True(window.Length >= OverlapEngine.MinimumSuggestionLength);
        });
    }

    private static Instant BaseInstant() => Monday.PlusDays(-3).AtMidnight().InZoneStrictly(Utc).ToInstant();

    private static AcceptedFriendProjection AcceptedProjection(UserId friendId) =>
        new(FriendshipId.New(), TestUser(friendId));

    private static User TestUser(UserId id)
    {
        User user = User.BeginOnboarding(id, FriendCode.Create($"PU-{Guid.NewGuid():N}"[..9]), AgeGateSource.SelfAttested, SystemClock.Instance);
        user.CompleteOnboarding(
            Username.Create($"u{Guid.NewGuid():N}"[..12]),
            DisplayName.Create("Fixture Friend"),
            Avatar.FromEmoji("🙂"),
            DateTimeZoneProviders.Tzdb["Etc/UTC"],
            Locale.Create("en"),
            SystemClock.Instance);
        return user;
    }

    private static AvailabilityOwnerData OwnerWithBlock(UserId ownerId, int startHour, int endHour) =>
        OwnerWithBlockMinutes(ownerId, startHour * 60, endHour * 60);

    private static AvailabilityOwnerData OwnerWithBlockMinutes(UserId ownerId, int startMinutes, int endMinutes)
    {
        Instant dayStart = Monday.AtMidnight().InZoneStrictly(Utc).ToInstant();
        Instant start = dayStart + Duration.FromMinutes(startMinutes);
        Instant end = dayStart + Duration.FromMinutes(endMinutes);
        AvailabilityBlock block = AvailabilityBlock.Create(ownerId, new TimeRange(start, end));
        return new AvailabilityOwnerData(ownerId, Utc, [block], [], []);
    }
}
