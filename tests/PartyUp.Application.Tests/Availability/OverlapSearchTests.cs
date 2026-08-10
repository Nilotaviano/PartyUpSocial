using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Availability;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §11 "Read stack (S4)": <see cref="FindOverlapWindowsUseCase"/>'s selection
/// resolution (D-110), A-078's silent-drop/no-per-id-resolution rules, D-108's default range and
/// no-truncation rule, §12-44's own-availability exclusion, and D-061's per-owner zone expansion.
/// </summary>
public sealed class OverlapSearchTests
{
    private static readonly UserId Viewer = UserId.New();
    private static readonly LocalDate Monday = new(2026, 9, 7); // an ISO Monday
    private static readonly DateTimeZone Utc = DateTimeZoneProviders.Tzdb["Etc/UTC"];

    [Fact]
    public async Task OmittedSelectionUsesEveryAcceptedFriend_D110()
    {
        UserId friendA = UserId.New();
        UserId friendB = UserId.New();
        FakeFriendshipReads friendships = new();
        friendships.Accepted.Add(AcceptedProjection(friendA));
        friendships.Accepted.Add(AcceptedProjection(friendB));
        FakeAvailabilityReads reads = new();
        reads.Owners.Add(OwnerWithBlock(friendA, Monday, 18, 20, Utc));
        reads.Owners.Add(OwnerWithBlock(friendB, Monday, 18, 20, Utc));

        FindOverlapWindowsUseCase useCase = new(friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(BaseInstant()));
        OverlapSearchResult result = await useCase.ExecuteAsync(Viewer, friendIds: null, range: null, default);

        Assert.Equal(SelectionSource.AllFriends, result.SelectionSource);
        OverlapWindow window = Assert.Single(result.Windows);
        Assert.Equal(2, window.AvailableCount);
        Assert.Contains(friendA, window.Available);
        Assert.Contains(friendB, window.Available);
    }

    [Fact]
    public async Task UnreadableOwnersAreExcludedNotMasked_A078()
    {
        UserId readable = UserId.New();
        UserId unreadable = UserId.New(); // not in the accepted set — stale/unfriended/blocked/tombstoned all take this path
        FakeFriendshipReads friendships = new();
        friendships.Accepted.Add(AcceptedProjection(readable));
        FakeAvailabilityReads reads = new();
        reads.Owners.Add(OwnerWithBlock(readable, Monday, 18, 20, Utc));
        reads.Owners.Add(OwnerWithBlock(unreadable, Monday, 18, 20, Utc));

        FindOverlapWindowsUseCase useCase = new(friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(BaseInstant()));
        OverlapSearchResult result = await useCase.ExecuteAsync(
            Viewer, friendIds: [readable, unreadable], range: null, default);

        OverlapWindow window = Assert.Single(result.Windows);
        Assert.Equal([readable], window.Available);
        Assert.DoesNotContain(unreadable, window.Available);
        Assert.DoesNotContain(unreadable, window.Maybe);
    }

    [Fact]
    public async Task ExplicitSelectionResponseCarriesNoPerIdResolution_A078()
    {
        UserId readable = UserId.New();
        UserId blocked = UserId.New();
        UserId tombstoned = UserId.New();
        UserId nonexistent = UserId.New();
        UserId unfriended = UserId.New();

        FakeFriendshipReads friendships = new();
        friendships.Accepted.Add(AcceptedProjection(readable));
        FakeAvailabilityReads reads = new();
        reads.Owners.Add(OwnerWithBlock(readable, Monday, 18, 20, Utc));

        FindOverlapWindowsUseCase useCaseWide = new(friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(BaseInstant()));
        OverlapSearchResult wide = await useCaseWide.ExecuteAsync(
            Viewer, friendIds: [readable, blocked, tombstoned, nonexistent, unfriended], range: null, default);

        FindOverlapWindowsUseCase useCaseNarrow = new(friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(BaseInstant()));
        OverlapSearchResult narrow = await useCaseNarrow.ExecuteAsync(
            Viewer, friendIds: [readable], range: null, default);

        Assert.Equal(narrow.SelectionSource, wide.SelectionSource);
        Assert.Equal(narrow.Range, wide.Range);
        Assert.Null(narrow.ConsideredFriendIds);
        Assert.Null(wide.ConsideredFriendIds);
        Assert.Equal(narrow.Windows.Count, wide.Windows.Count);
        for (int i = 0; i < narrow.Windows.Count; i++)
        {
            Assert.Equal(narrow.Windows[i].Range, wide.Windows[i].Range);
            Assert.Equal(narrow.Windows[i].Available, wide.Windows[i].Available);
            Assert.Equal(narrow.Windows[i].Maybe, wide.Windows[i].Maybe);
        }
    }

    [Fact]
    public async Task ConsideredFriendsIsPresentOnlyForTheAllFriendsSelection_A078_D110()
    {
        UserId friendA = UserId.New();
        FakeFriendshipReads friendships = new();
        friendships.Accepted.Add(AcceptedProjection(friendA));
        FakeAvailabilityReads reads = new();
        reads.Owners.Add(OwnerWithBlock(friendA, Monday, 18, 20, Utc));

        FindOverlapWindowsUseCase useCase = new(friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(BaseInstant()));

        OverlapSearchResult explicitResult = await useCase.ExecuteAsync(Viewer, [friendA], null, default);
        Assert.Null(explicitResult.ConsideredFriendIds);

        OverlapSearchResult allFriendsResult = await useCase.ExecuteAsync(Viewer, null, null, default);
        Assert.NotNull(allFriendsResult.ConsideredFriendIds);
        Assert.Equal([friendA], allFriendsResult.ConsideredFriendIds);
    }

    [Fact]
    public async Task RangeDefaultsToSevenDaysAndRejectsBeyondNinetyTwo_D108()
    {
        FakeFriendshipReads friendships = new();
        FakeAvailabilityReads reads = new();
        Instant now = BaseInstant();
        FindOverlapWindowsUseCase useCase = new(friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(now));

        OverlapSearchResult result = await useCase.ExecuteAsync(Viewer, null, null, default);
        Assert.Equal(now, result.Range.Start);
        Assert.Equal(now + Duration.FromDays(OverlapEngine.DefaultRangeDays), result.Range.End);

        TimeRange tooLarge = new(now, now + Duration.FromDays(OverlapEngine.MaximumRangeDays) + Duration.FromMinutes(1));
        await Assert.ThrowsAsync<DomainException>(() => useCase.ExecuteAsync(Viewer, null, tooLarge, default));
    }

    [Fact]
    public async Task ResultsAreNeverTruncated_D108()
    {
        const int FriendCount = 25;
        FakeFriendshipReads friendships = new();
        FakeAvailabilityReads reads = new();
        for (int i = 0; i < FriendCount; i++)
        {
            UserId friend = UserId.New();
            friendships.Accepted.Add(AcceptedProjection(friend));
            // Each friend's block starts one hour later than the last but all share the same far-out
            // end, producing FriendCount distinct nested windows — a count well past any plausible
            // display cap (e.g. Home's five), so a silent truncation would be observable here.
            reads.Owners.Add(OwnerWithBlock(friend, Monday, i, 100, Utc));
        }

        FindOverlapWindowsUseCase useCase = new(friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(BaseInstant()));
        OverlapSearchResult result = await useCase.ExecuteAsync(Viewer, null, null, default);

        Assert.Equal(FriendCount, result.Windows.Count);
    }

    [Fact]
    public async Task ViewerOwnAvailabilityIsNotAnInput_D035()
    {
        // The viewer never appears as a selectable owner: FindOverlapWindowsUseCase resolves the
        // selection from the friend graph only, and the fake's read-repository call proves the
        // viewer's id is never among the owners the port is asked to read (§12-44).
        UserId friendA = UserId.New();
        FakeFriendshipReads friendships = new();
        friendships.Accepted.Add(AcceptedProjection(friendA));
        FakeAvailabilityReads reads = new();
        reads.Owners.Add(OwnerWithBlock(friendA, Monday, 18, 20, Utc));
        reads.Owners.Add(OwnerWithBlock(Viewer, Monday, 18, 20, Utc)); // present in the store, never requested

        FindOverlapWindowsUseCase useCase = new(friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(BaseInstant()));
        await useCase.ExecuteAsync(Viewer, null, null, default);

        Assert.DoesNotContain(Viewer, reads.RequestedOwnerIds.SelectMany(ids => ids));
    }

    [Fact]
    public async Task ExpansionUsesEachOwnersZone_D061()
    {
        // Two owners hold the identical local wall-clock pattern (Monday 18:00-20:00) but in zones
        // three hours apart. If expansion ignored each owner's own zone the two occurrences would
        // coincide; instead they only partially overlap, which is observable as a Maybe (not
        // Available) count on the shared sub-window.
        DateTimeZone zoneA = DateTimeZoneProviders.Tzdb["Etc/GMT-3"]; // UTC+3
        DateTimeZone zoneB = DateTimeZoneProviders.Tzdb["Etc/GMT+0"]; // UTC

        UserId ownerA = UserId.New();
        UserId ownerB = UserId.New();
        FakeFriendshipReads friendships = new();
        friendships.Accepted.Add(AcceptedProjection(ownerA));
        friendships.Accepted.Add(AcceptedProjection(ownerB));
        FakeAvailabilityReads reads = new();
        reads.Owners.Add(OwnerWithSeries(ownerA, zoneA));
        reads.Owners.Add(OwnerWithSeries(ownerB, zoneB));

        FindOverlapWindowsUseCase useCase = new(friendships, reads, DateTimeZoneProviders.Tzdb, new FixedClock(BaseInstant()));
        OverlapSearchResult result = await useCase.ExecuteAsync(Viewer, null, null, default);

        // A's 18:00-20:00 UTC+3 is 15:00-17:00 UTC; B's is 18:00-20:00 UTC. They do not overlap at
        // all, so both owners are individually available across the full range but never together.
        Assert.DoesNotContain(result.Windows, w => w.AvailableCount == 2);
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

    private static AvailabilityOwnerData OwnerWithBlock(
        UserId ownerId, LocalDate date, int startHour, int endHour, DateTimeZone zone)
    {
        Instant start = date.AtMidnight().InZoneStrictly(zone).ToInstant() + Duration.FromHours(startHour);
        Instant end = date.AtMidnight().InZoneStrictly(zone).ToInstant() + Duration.FromHours(endHour);
        AvailabilityBlock block = AvailabilityBlock.Create(ownerId, new TimeRange(start, end));
        return new AvailabilityOwnerData(ownerId, zone, [block], [], []);
    }

    private static AvailabilityOwnerData OwnerWithSeries(UserId ownerId, DateTimeZone zone)
    {
        AvailabilitySeries series = AvailabilitySeries.Create(
            ownerId,
            WeeklyPattern.Create(Monday.DayOfWeek, new LocalTime(18, 0), Duration15.FromMinutes(120)),
            zone,
            Monday.PlusDays(-7));
        return new AvailabilityOwnerData(ownerId, zone, [], [series], []);
    }
}

/// <summary>Shared in-memory fakes for the S4 Application read-stack tests.</summary>
internal sealed class FakeFriendshipReads : IFriendshipRepository
{
    internal List<AcceptedFriendProjection> Accepted { get; } = [];

    internal int ListAcceptedCalls { get; private set; }

    public Task<IReadOnlyList<AcceptedFriendProjection>> ListAcceptedAsync(UserId userId, CancellationToken cancellationToken)
    {
        ListAcceptedCalls++;
        return Task.FromResult<IReadOnlyList<AcceptedFriendProjection>>([.. Accepted]);
    }

    public Task<Friendship?> FindPairAsync(UserId first, UserId second, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Friendship?> FindPairForUpdateAsync(UserId first, UserId second, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<FriendshipPair?> FindOwnedPairAsync(FriendshipId friendshipId, UserId actorId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Friendship?> FindByIdForUpdateAsync(FriendshipId friendshipId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<PendingFriendProjection>> ListPendingAsync(UserId userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Add(Friendship friendship) => throw new NotSupportedException();

    public void Remove(Friendship friendship) => throw new NotSupportedException();
}

internal sealed class FakeAvailabilityReads : IAvailabilityReadRepository
{
    internal List<AvailabilityOwnerData> Owners { get; } = [];

    internal List<IReadOnlyCollection<UserId>> RequestedOwnerIds { get; } = [];

    internal int ReadCalls { get; private set; }

    public Task<IReadOnlyList<AvailabilityOwnerData>> ReadOwnersWindowAsync(
        IReadOnlyCollection<UserId> ownerIds, TimeRange window, CancellationToken cancellationToken)
    {
        ReadCalls++;
        RequestedOwnerIds.Add(ownerIds);
        HashSet<UserId> ids = [.. ownerIds];
        return Task.FromResult<IReadOnlyList<AvailabilityOwnerData>>(
            [.. Owners.Where(o => ids.Contains(o.OwnerId))]);
    }

    public Task<IReadOnlyList<AvailabilitySeries>> ReadOwnerSeriesAsync(
        UserId ownerId, Instant notEndedBefore, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AvailabilitySeries>>(
            [.. Owners.Where(o => o.OwnerId == ownerId).SelectMany(o => o.Series)]);
}

internal sealed class FixedClock(Instant now) : IClock
{
    public Instant GetCurrentInstant() => now;
}
