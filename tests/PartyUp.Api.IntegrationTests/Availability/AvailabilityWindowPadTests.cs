using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Availability;
using PartyUp.Domain.Availability;
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
