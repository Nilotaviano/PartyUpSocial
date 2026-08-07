using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Availability;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §4.2/A-076: the fixed three-day pad on <c>ReadOwnersWindowAsync</c>'s series
/// fetch. Exercised at the repository level per §4.2 (d) — <c>/calendar</c>, <c>/me/availability</c>
/// and an overlap search share this one read (§5.1), so a repository-level assertion already covers
/// every surface that will call it once the HTTP layer exists (S5).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityWindowPadTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task OccurrencesLiveAtTheWindowEdgeAreFetchedAndOnesOutsideItAreDiscarded_A076()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId ownerId = await SeedUserAsync(context);

        DateTimeZone utcMinus12 = DateTimeZoneProviders.Tzdb["Etc/GMT+12"];
        DateTimeZone utcPlus14 = DateTimeZoneProviders.Tzdb["Etc/GMT-14"];
        LocalDate d = new(2026, 10, 5);

        // (a) trailing: UTC-12, effective_from=d, effective_until=d+1, start 23:45, 24h — live in UTC
        // until d+2 11:45. A window starting d+2 06:00 must still fetch this row.
        AvailabilitySeries trailing = AvailabilitySeries.Create(
            ownerId,
            WeeklyPattern.Create(d.DayOfWeek, new LocalTime(23, 45), Duration15.FromMinutes(24 * 60)),
            utcMinus12,
            d,
            d.PlusDays(1));

        // (b) leading: UTC+14, effective_from=d, start 00:00 — live from d-1 10:00 UTC. A window
        // ending d-1 12:00 must fetch this row even though `effective_from` is d, after the window.
        AvailabilitySeries leading = AvailabilitySeries.Create(
            ownerId,
            WeeklyPattern.Create(d.DayOfWeek, new LocalTime(0, 0), Duration15.FromMinutes(15)),
            utcPlus14,
            d);

        context.AvailabilitySeries.AddRange(trailing, leading);
        await context.SaveChangesAsync();

        await using AppDbContext readContext = CreateContext();
        var repository = new AvailabilityReadRepository(readContext);

        // (a) the trailing row is fetched for a window starting d+2 06:00.
        TimeRange trailingWindow = new(
            d.PlusDays(2).AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant() + Duration.FromHours(6),
            d.PlusDays(3).AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant());
        IReadOnlyList<AvailabilityOwnerData> trailingRead = await repository.ReadOwnersWindowAsync(
            [ownerId], trailingWindow, default);
        Assert.Contains(trailingRead.Single().Series, s => s.Id == trailing.Id);

        // The occurrence itself, once projected, is inside the true window.
        IReadOnlyList<TimeRange> trailingProjected = AvailabilityProjection.Project(
            [], trailingRead.Single().Series, [], trailingWindow, DateTimeZoneProviders.Tzdb, utcMinus12);
        Assert.NotEmpty(trailingProjected);

        // (b) the leading row is fetched for a window ending d-1 12:00.
        TimeRange leadingWindow = new(
            d.PlusDays(-3).AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant(),
            d.PlusDays(-1).AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant() + Duration.FromHours(12));
        IReadOnlyList<AvailabilityOwnerData> leadingRead = await repository.ReadOwnersWindowAsync(
            [ownerId], leadingWindow, default);
        Assert.Contains(leadingRead.Single().Series, s => s.Id == leading.Id);
        IReadOnlyList<TimeRange> leadingProjected = AvailabilityProjection.Project(
            [], leadingRead.Single().Series, [], leadingWindow, DateTimeZoneProviders.Tzdb, utcPlus14);
        Assert.NotEmpty(leadingProjected);

        // (c) the trailing row is still admitted (padded) for a window well before its real
        // occurrence, but projection over the true window then contributes zero occurrences.
        TimeRange beforeWindow = new(
            d.PlusDays(-2).AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant(),
            d.AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant());
        IReadOnlyList<AvailabilityOwnerData> admittedButEmptyRead = await repository.ReadOwnersWindowAsync(
            [ownerId], beforeWindow, default);
        Assert.Contains(admittedButEmptyRead.Single().Series, s => s.Id == trailing.Id);
        List<AvailabilitySeries> trailingOnly = [.. admittedButEmptyRead.Single().Series.Where(s => s.Id == trailing.Id)];
        IReadOnlyList<TimeRange> emptyProjected = AvailabilityProjection.Project(
            [], trailingOnly, [], beforeWindow, DateTimeZoneProviders.Tzdb, utcMinus12);
        Assert.Empty(emptyProjected);

        // (d) the same read, called again with the same arguments — standing in for the three D-149
        // read surfaces that will all route through this one method (§5.1) — returns the same rows.
        IReadOnlyList<AvailabilityOwnerData> secondCall = await repository.ReadOwnersWindowAsync(
            [ownerId], trailingWindow, default);
        Assert.Equal(
            trailingRead.Single().Series.Select(s => s.Id),
            secondCall.Single().Series.Select(s => s.Id));
    }

    /// <summary>
    /// PHASE5_DESIGN.md S2 verification carry C3: §4.2 pad case (d) was proven tautologically in S2
    /// (the same method, <c>ReadOwnersWindowAsync</c>, called twice). Now that <c>/calendar</c>,
    /// <c>/me/availability</c> and the overlap search exist as S4 use cases, this proves the trailing
    /// boundary series appears identically through all three: <see cref="ReadMyAvailabilityUseCase"/>,
    /// <see cref="GetCalendarViewUseCase"/>'s <c>myAvailability</c> layer, and
    /// <see cref="FindOverlapWindowsUseCase"/>'s read — the same one-read composition (§5.1), not
    /// three independent implementations that could silently diverge.
    /// </summary>
    [Fact]
    public async Task BoundarySeriesAppearIdenticallyThroughMyAvailabilityCalendarAndOverlapReads_C3()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId ownerId = await SeedUserAsync(context);
        UserId viewerId = await SeedUserAsync(context);
        await MakeAcceptedFriendsAsync(context, viewerId, ownerId);

        DateTimeZone utcMinus12 = DateTimeZoneProviders.Tzdb["Etc/GMT+12"];
        LocalDate d = new(2026, 10, 5);

        // The same (a) trailing boundary series AvailabilityWindowPadTests proves at the repository
        // level — live in UTC until d+2 11:45, admitted only by the A-076 pad for a window starting
        // d+2 06:00.
        AvailabilitySeries trailing = AvailabilitySeries.Create(
            ownerId,
            WeeklyPattern.Create(d.DayOfWeek, new LocalTime(23, 45), Duration15.FromMinutes(24 * 60)),
            utcMinus12,
            d,
            d.PlusDays(1));

        context.AvailabilitySeries.Add(trailing);
        await context.SaveChangesAsync();

        TimeRange trailingWindow = new(
            d.PlusDays(2).AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant() + Duration.FromHours(6),
            d.PlusDays(3).AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant());

        await using AppDbContext readContext = CreateContext();
        var availabilityReads = new AvailabilityReadRepository(readContext);
        var friendships = new FriendshipRepository(readContext);
        var sessionReads = new SessionReadRepository(readContext);

        ReadMyAvailabilityUseCase myAvailability = new(availabilityReads, DateTimeZoneProviders.Tzdb);
        MyAvailabilityLayer myLayer = await myAvailability.ExecuteAsync(ownerId, trailingWindow, default);
        Assert.Contains(myLayer.Occurrences, occurrence =>
            occurrence.Origin is AvailabilityOccurrenceOrigin.FromSeriesOccurrence fromSeries
            && fromSeries.SeriesId == trailing.Id);

        GetCalendarViewUseCase calendarView = new(
            myAvailability, friendships, availabilityReads, sessionReads, DateTimeZoneProviders.Tzdb);
        CalendarView calendar = await calendarView.ExecuteAsync(ownerId, trailingWindow, default);
        Assert.Contains(calendar.MyAvailability.Occurrences, occurrence =>
            occurrence.Origin is AvailabilityOccurrenceOrigin.FromSeriesOccurrence fromSeries
            && fromSeries.SeriesId == trailing.Id);

        FindOverlapWindowsUseCase overlapSearch = new(
            friendships, availabilityReads, DateTimeZoneProviders.Tzdb, new FixedInstantClock(trailingWindow.Start));
        OverlapSearchResult overlap = await overlapSearch.ExecuteAsync(
            viewerId, [ownerId], trailingWindow, default);
        Assert.NotEmpty(overlap.Windows);
    }

    private static async Task MakeAcceptedFriendsAsync(AppDbContext context, UserId a, UserId b)
    {
        Friendship friendship = Friendship.Request(
            await context.DomainUsers.SingleAsync(u => u.Id == a),
            await context.DomainUsers.SingleAsync(u => u.Id == b),
            SystemClock.Instance);
        friendship.Accept(b, SystemClock.Instance);
        _ = friendship.DequeueEvents();
        context.Friendships.Add(friendship);
        await context.SaveChangesAsync();
    }

    private static async Task<UserId> SeedUserAsync(AppDbContext context)
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create($"PU-{Guid.NewGuid():N}"[..9]),
            AgeGateSource.SelfAttested,
            SystemClock.Instance);
        string suffix = Guid.NewGuid().ToString("N")[..8];
        user.CompleteOnboarding(
            Username.Create($"user{suffix}"),
            DisplayName.Create("Window Pad Owner"),
            Avatar.FromEmoji("🙂"),
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"),
            SystemClock.Instance);
        context.DomainUsers.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}

file sealed class FixedInstantClock(Instant now) : IClock
{
    public Instant GetCurrentInstant() => now;
}
