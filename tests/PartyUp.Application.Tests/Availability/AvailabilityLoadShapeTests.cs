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

    /// <summary>
    /// Round-1 verification F3: a fixture where every friend shares identical instants is structurally
    /// unfailable — it collapses to a handful of windows regardless of friend count, so the 4096
    /// ceiling is never a live constraint. This one staggers each friend's 20 blocks into 20
    /// widely-spaced day groups (4 days apart, so groups never overlap each other) and staggers the
    /// starts *within* a group across a 64-friend cycle (still a 15-minute-grid offset), which is
    /// enough distinct boundary instants to produce a four-figure window count without adversarially
    /// exploding past the ceiling.
    /// </summary>
    [Fact]
    public async Task OneHundredFiftyFriendsOverNinetyTwoDaysStaysWithinFourThousandWindowsAndTwoPortCalls_A079()
    {
        const int FriendCount = 150;
        const int SeriesPerFriend = 3;
        const int BlocksPerFriend = 20;
        const int StaggerCycle = 64; // distinct starts per day group; 64 * 15 min = 16 h, inside the group's own day.

        Instant windowStart = Monday.PlusDays(-14).AtMidnight().InZoneStrictly(Utc).ToInstant();
        TimeRange window = new(windowStart, windowStart + Duration.FromDays(92));

        FakeFriendshipReads friendships = new();
        FakeAvailabilityReads reads = new();

        // The documented worst-case counts. Series stay identical across friends (D-108's Home
        // suggestion floor is a Domain concern, not this budget's) — the staggering that makes the
        // ceiling live lives entirely in the one-time blocks below.
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
                // Each block index is its own day group, 4 days apart (20 groups span 80 days, inside
                // the 92-day window with margin) so groups never share a boundary instant with each
                // other. Within a group, the start is staggered by (f mod 64) * 15 minutes and the end
                // is shared by every friend in the group — the same shared-end ladder shape that makes
                // a nested set of windows realizable, just replayed across 20 independent groups.
                int dayGroup = b * 4;
                int staggerMinutes = (f % StaggerCycle) * 15;
                Instant groupDayStart = windowStart + Duration.FromDays(dayGroup);
                Instant start = groupDayStart + Duration.FromMinutes(staggerMinutes);
                Instant end = groupDayStart + Duration.FromHours(22);
                blocks.Add(AvailabilityBlock.Create(friend, new TimeRange(start, end)));
            }

            reads.Owners.Add(new AvailabilityOwnerData(friend, Utc, blocks, series, []));
        }

        FindOverlapWindowsUseCase useCase = new(friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(windowStart));
        OverlapSearchResult result = await useCase.ExecuteAsync(UserId.New(), friendIds: null, range: window, default);

        // Floor proves the fixture is dense enough that the ceiling is a live constraint, not a
        // structurally-unfailable formality (round-1 F3); ceiling is A-079's actual budget.
        Assert.True(
            result.Windows.Count >= 1000,
            $"Fixture too sparse to exercise the ceiling: expected >= 1000 windows, got {result.Windows.Count}.");
        Assert.True(result.Windows.Count <= 4096, $"Expected at most 4096 windows, got {result.Windows.Count}.");
        Assert.Equal(1, reads.ReadCalls);
        Assert.Equal(1, friendships.ListAcceptedCalls);
    }

    private static AcceptedFriendProjection AcceptedProjection(UserId friendId) =>
        new(FriendshipId.New(), new FriendCounterpart(friendId, null, null, null, []));
}
