using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §4.2's DST gates, against real Postgres in this slice (not delegated to Phase 1):
/// round-trip a series through the database, then expand it and prove the wall-clock arithmetic still
/// holds (§12-19, DOMAIN_MODEL.md:195).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityDstTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task SpringForwardDropsFallBackLengthensAndMidnightCarries_D061_D132()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId ownerId = await SeedUserAsync(context);

        DateTimeZone newYork = DateTimeZoneProviders.Tzdb["America/New_York"];
        DateTimeZone sydney = DateTimeZoneProviders.Tzdb["Australia/Sydney"];

        // 2027-03-14: America/New_York springs forward 02:00 -> 03:00. A 15-minute occurrence wholly
        // inside the gap collapses to zero length and is dropped.
        AvailabilitySeries springForward = AvailabilitySeries.Create(
            ownerId,
            WeeklyPattern.Create(IsoDayOfWeek.Sunday, new LocalTime(2, 30), Duration15.FromMinutes(15)),
            newYork,
            new LocalDate(2027, 3, 7));

        // 2026-11-01: America/New_York falls back 02:00 -> 01:00, repeating the 01:00-02:00 hour. A
        // series spanning local 00:30-02:30 (2h wall-clock) is 3h in real time.
        AvailabilitySeries fallBack = AvailabilitySeries.Create(
            ownerId,
            WeeklyPattern.Create(IsoDayOfWeek.Sunday, new LocalTime(0, 30), Duration15.FromMinutes(120)),
            newYork,
            new LocalDate(2026, 10, 25));

        // A late block carries into the next calendar day rather than wrapping (§12-19): 23:00 + 2h.
        AvailabilitySeries midnightCarry = AvailabilitySeries.Create(
            ownerId,
            WeeklyPattern.Create(IsoDayOfWeek.Monday, new LocalTime(23, 0), Duration15.FromMinutes(120)),
            newYork,
            new LocalDate(2026, 9, 7));

        // Southern hemisphere: an ordinary week with no transition round-trips and expands normally.
        AvailabilitySeries southern = AvailabilitySeries.Create(
            ownerId,
            WeeklyPattern.Create(IsoDayOfWeek.Friday, new LocalTime(19, 0), Duration15.FromMinutes(90)),
            sydney,
            new LocalDate(2026, 9, 1));

        context.AvailabilitySeries.AddRange(springForward, fallBack, midnightCarry, southern);
        await context.SaveChangesAsync();

        await using AppDbContext readContext = CreateContext();

        AvailabilitySeries reloadedSpringForward = await readContext.AvailabilitySeries
            .AsNoTracking().SingleAsync(s => s.Id == springForward.Id);
        DateTimeZone resolvedSpringForwardZone = AvailabilityExpansion.ResolveZone(
            reloadedSpringForward, DateTimeZoneProviders.Tzdb, newYork);
        TimeRange springWindow = new(
            Instant.FromUtc(2027, 3, 14, 0, 0), Instant.FromUtc(2027, 3, 14, 12, 0));
        IReadOnlyList<AvailabilityOccurrence> springOccurrences = AvailabilityExpansion.ExpandIn(
            reloadedSpringForward, [], springWindow, resolvedSpringForwardZone);
        Assert.Empty(springOccurrences);

        AvailabilitySeries reloadedFallBack = await readContext.AvailabilitySeries
            .AsNoTracking().SingleAsync(s => s.Id == fallBack.Id);
        DateTimeZone resolvedFallBackZone = AvailabilityExpansion.ResolveZone(
            reloadedFallBack, DateTimeZoneProviders.Tzdb, newYork);
        TimeRange fallWindow = new(
            Instant.FromUtc(2026, 11, 1, 0, 0), Instant.FromUtc(2026, 11, 1, 12, 0));
        IReadOnlyList<AvailabilityOccurrence> fallOccurrences = AvailabilityExpansion.ExpandIn(
            reloadedFallBack, [], fallWindow, resolvedFallBackZone);
        AvailabilityOccurrence fallOccurrence = Assert.Single(fallOccurrences);
        Assert.Equal(Duration.FromHours(3), fallOccurrence.Range.Length);

        AvailabilitySeries reloadedMidnightCarry = await readContext.AvailabilitySeries
            .AsNoTracking().SingleAsync(s => s.Id == midnightCarry.Id);
        DateTimeZone resolvedMidnightZone = AvailabilityExpansion.ResolveZone(
            reloadedMidnightCarry, DateTimeZoneProviders.Tzdb, newYork);
        TimeRange midnightWindow = new(
            Instant.FromUtc(2026, 9, 7, 0, 0), Instant.FromUtc(2026, 9, 9, 12, 0));
        IReadOnlyList<AvailabilityOccurrence> midnightOccurrences = AvailabilityExpansion.ExpandIn(
            reloadedMidnightCarry, [], midnightWindow, resolvedMidnightZone);
        AvailabilityOccurrence midnightOccurrence = Assert.Single(midnightOccurrences);
        LocalDate startDate = midnightOccurrence.Range.Start.InZone(resolvedMidnightZone).Date;
        LocalDate endDate = midnightOccurrence.Range.End.InZone(resolvedMidnightZone).Date;
        Assert.Equal(startDate.PlusDays(1), endDate);
        Assert.Equal(Duration.FromHours(2), midnightOccurrence.Range.Length);

        AvailabilitySeries reloadedSouthern = await readContext.AvailabilitySeries
            .AsNoTracking().SingleAsync(s => s.Id == southern.Id);
        DateTimeZone resolvedSouthernZone = AvailabilityExpansion.ResolveZone(
            reloadedSouthern, DateTimeZoneProviders.Tzdb, sydney);
        TimeRange southernWindow = new(
            Instant.FromUtc(2026, 9, 4, 0, 0), Instant.FromUtc(2026, 9, 5, 12, 0));
        IReadOnlyList<AvailabilityOccurrence> southernOccurrences = AvailabilityExpansion.ExpandIn(
            reloadedSouthern, [], southernWindow, resolvedSouthernZone);
        AvailabilityOccurrence southernOccurrence = Assert.Single(southernOccurrences);
        Assert.Equal(Duration.FromMinutes(90), southernOccurrence.Range.Length);
    }

    private static async Task<UserId> SeedUserAsync(AppDbContext context)
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create($"PU-{Guid.NewGuid():N}"[..9]),
            AgeGateSource.SelfAttested,
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
