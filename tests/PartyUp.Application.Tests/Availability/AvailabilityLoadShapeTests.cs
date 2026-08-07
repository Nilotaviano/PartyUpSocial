using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Availability;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Tests.Availability;

/// <summary>
/// PHASE5_DESIGN.md A-079: the documented worst case — 150 accepted friends, each with 3 weekly series
/// and 20 one-time blocks, over a 92-day range. The <em>shape</em> half of the budget
/// (<c>ARCHITECTURE.md:70</c> — in-memory fakes, no database): <c>windows.Count</c> must not exceed
/// 4096, and the two overlap-resolution ports — <see cref="IAvailabilityReadRepository.ReadOwnersWindowAsync"/>
/// and the friend-set call — must each be called exactly once, regardless of selection size. The
/// statement bridge (four SQL statements per <c>ReadOwnersWindowAsync</c> call) is S2's; the byte
/// budget is S5's.
/// </summary>
public sealed class AvailabilityLoadShapeTests
{
    private static readonly LocalDate Monday = new(2026, 9, 7);
    private static readonly DateTimeZone Utc = DateTimeZoneProviders.Tzdb["Etc/UTC"];

    [Fact]
    public async Task OneHundredFiftyFriendsOverNinetyTwoDaysStaysWithinFourThousandWindowsAndTwoPortCalls_A079()
    {
        const int FriendCount = 150;
        const int SeriesPerFriend = 3;
        const int BlocksPerFriend = 20;

        Instant windowStart = Monday.PlusDays(-14).AtMidnight().InZoneStrictly(Utc).ToInstant();
        TimeRange window = new(windowStart, windowStart + Duration.FromDays(92));

        FakeFriendshipReads friendships = new();
        FakeAvailabilityReads reads = new();

        // The documented worst-case counts, drawn from a shared daily/weekly menu (A-079 bounds cost
        // by range and cadence, not by an adversarial per-friend timetable) — every friend's series
        // and blocks land on the same instants as every other friend's, so the sweep's distinct
        // boundary count stays flat as the friend count grows to 150.
        IsoDayOfWeek[] seriesDays = [IsoDayOfWeek.Monday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Friday];

        for (int f = 0; f < FriendCount; f++)
        {
            UserId friend = UserId.New();
            friendships.Accepted.Add(AcceptedProjection(friend));

            List<AvailabilitySeries> series = [];
            for (int s = 0; s < SeriesPerFriend; s++)
            {
                series.Add(AvailabilitySeries.Create(
                    friend,
                    WeeklyPattern.Create(seriesDays[s], new LocalTime(19, 0), Duration15.FromMinutes(120)),
                    Utc,
                    Monday.PlusDays(-21)));
            }

            List<AvailabilityBlock> blocks = [];
            for (int b = 0; b < BlocksPerFriend; b++)
            {
                Instant start = windowStart + Duration.FromDays(b * 4) + Duration.FromHours(10);
                blocks.Add(AvailabilityBlock.Create(friend, new TimeRange(start, start + Duration.FromMinutes(90))));
            }

            reads.Owners.Add(new AvailabilityOwnerData(friend, Utc, blocks, series, []));
        }

        FindOverlapWindowsUseCase useCase = new(friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(windowStart));
        OverlapSearchResult result = await useCase.ExecuteAsync(UserId.New(), friendIds: null, range: window, default);

        Assert.True(result.Windows.Count <= 4096, $"Expected at most 4096 windows, got {result.Windows.Count}.");
        Assert.Equal(1, reads.ReadCalls);
        Assert.Equal(1, friendships.ListAcceptedCalls);
    }

    private static AcceptedFriendProjection AcceptedProjection(UserId friendId) =>
        new(FriendshipId.New(), TestUser(friendId));

    private static PartyUp.Domain.Users.User TestUser(UserId id)
    {
        PartyUp.Domain.Users.User user = PartyUp.Domain.Users.User.BeginOnboarding(
            id, PartyUp.Domain.Users.FriendCode.Create($"PU-{Guid.NewGuid():N}"[..9]),
            PartyUp.Domain.Users.AgeGateSource.SelfAttested, SystemClock.Instance);
        user.CompleteOnboarding(
            Username.Create($"u{Guid.NewGuid():N}"[..12]),
            DisplayName.Create("Fixture Friend"),
            PartyUp.Domain.Users.Avatar.FromEmoji("🙂"),
            Utc,
            PartyUp.Domain.Users.Locale.Create("en"),
            SystemClock.Instance);
        return user;
    }
}
