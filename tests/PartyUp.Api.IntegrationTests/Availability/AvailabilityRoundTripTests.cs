using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §4.2: save, clear the context, reload, and prove semantic equality of every field
/// a round trip could silently corrupt — plus §12-29's unknown-tzdb-zone fallback.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityRoundTripTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task SeriesBlocksSkipsAndProvenanceSurvive_D143_D133()
    {
        await using AppDbContext writeContext = CreateContext();
        await writeContext.Database.MigrateAsync();
        UserId ownerId = await SeedUserAsync(writeContext);

        DateTimeZone zone = DateTimeZoneProviders.Tzdb["America/Fortaleza"];
        WeeklyPattern openEndedPattern = WeeklyPattern.Create(
            IsoDayOfWeek.Monday, new LocalTime(18, 0), Duration15.FromMinutes(90));
        AvailabilitySeries openEnded = AvailabilitySeries.Create(
            ownerId, openEndedPattern, zone, new LocalDate(2026, 9, 1));

        WeeklyPattern boundedPattern = WeeklyPattern.Create(
            IsoDayOfWeek.Wednesday, new LocalTime(20, 30), Duration15.FromMinutes(60));
        AvailabilitySeries bounded = AvailabilitySeries.Create(
            ownerId, boundedPattern, zone, new LocalDate(2026, 9, 1), new LocalDate(2026, 12, 1));

        Instant oneTimeStart = Instant.FromUtc(2026, 9, 10, 14, 0);
        AvailabilityBlock oneTimeBlock = AvailabilityBlock.Create(
            ownerId, new TimeRange(oneTimeStart, oneTimeStart + Duration.FromMinutes(45)));

        LocalDate materializedDate = new(2026, 9, 7);
        AvailabilityOccurrenceSkip skip = AvailabilityOccurrenceSkip.For(openEnded, materializedDate);
        Instant materializedStart = Instant.FromUtc(2026, 9, 7, 22, 0);
        AvailabilityBlock materializedBlock = AvailabilityBlock.Materialize(
            openEnded, materializedDate, new TimeRange(materializedStart, materializedStart + Duration.FromMinutes(30)));

        writeContext.AvailabilitySeries.AddRange(openEnded, bounded);
        writeContext.AvailabilityOccurrenceSkips.Add(skip);
        writeContext.AvailabilityBlocks.AddRange(oneTimeBlock, materializedBlock);
        await writeContext.SaveChangesAsync();

        await using AppDbContext readContext = CreateContext();

        AvailabilitySeries reloadedOpenEnded = await readContext.AvailabilitySeries
            .AsNoTracking()
            .SingleAsync(series => series.Id == openEnded.Id);
        Assert.Equal(openEnded.DayOfWeek, reloadedOpenEnded.DayOfWeek);
        Assert.Equal(openEnded.StartTime, reloadedOpenEnded.StartTime);
        Assert.Equal(openEnded.Length, reloadedOpenEnded.Length);
        Assert.Equal(openEnded.ZoneId, reloadedOpenEnded.ZoneId);
        Assert.Equal(openEnded.EffectiveFrom, reloadedOpenEnded.EffectiveFrom);
        Assert.Null(reloadedOpenEnded.EffectiveUntil);

        AvailabilitySeries reloadedBounded = await readContext.AvailabilitySeries
            .AsNoTracking()
            .SingleAsync(series => series.Id == bounded.Id);
        Assert.Equal(bounded.EffectiveFrom, reloadedBounded.EffectiveFrom);
        Assert.Equal(bounded.EffectiveUntil, reloadedBounded.EffectiveUntil);

        AvailabilityOccurrenceSkip reloadedSkip = await readContext.AvailabilityOccurrenceSkips
            .AsNoTracking()
            .SingleAsync(s => s.SeriesId == openEnded.Id);
        Assert.Equal(materializedDate, reloadedSkip.OccurrenceDate);

        AvailabilityBlock reloadedOneTime = await readContext.AvailabilityBlocks
            .AsNoTracking()
            .SingleAsync(block => block.Id == oneTimeBlock.Id);
        Assert.Equal(oneTimeBlock.Start, reloadedOneTime.Start);
        Assert.Equal(oneTimeBlock.End, reloadedOneTime.End);
        Assert.Null(reloadedOneTime.SourceSeriesId);
        Assert.Null(reloadedOneTime.SourceOccurrenceDate);

        AvailabilityBlock reloadedMaterialized = await readContext.AvailabilityBlocks
            .AsNoTracking()
            .SingleAsync(block => block.Id == materializedBlock.Id);
        Assert.Equal(materializedBlock.Start, reloadedMaterialized.Start);
        Assert.Equal(materializedBlock.End, reloadedMaterialized.End);
        Assert.Equal(openEnded.Id, reloadedMaterialized.SourceSeriesId);
        Assert.Equal(materializedDate, reloadedMaterialized.SourceOccurrenceDate);
    }

    [Fact]
    public async Task UnknownTzdbZoneIdStillLoadsAndExpands_D061()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId ownerId = await SeedUserAsync(context);

        // Bypasses Domain's zone.Id validation on purpose — this proves the column round-trips a
        // tzdb id that has since left the registry (§12-29), which AvailabilitySeries.Create cannot
        // itself express since it always takes a resolved DateTimeZone.
        Guid seriesId = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO availability_series
                (id, owner_id, day_of_week, start_time, duration_minutes, zone_id, effective_from)
            VALUES
                ({seriesId}, {ownerId.Value}, 2, {new LocalTime(19, 0)}, 60, 'Retired/NoLongerAZone',
                 {new LocalDate(2026, 9, 1)})
            """);

        await using AppDbContext readContext = CreateContext();
        AvailabilitySeries reloaded = await readContext.AvailabilitySeries
            .AsNoTracking()
            .SingleAsync(series => series.Id == AvailabilitySeriesId.From(seriesId));
        Assert.Equal("Retired/NoLongerAZone", reloaded.ZoneId);

        DateTimeZone ownerZone = DateTimeZoneProviders.Tzdb["America/Fortaleza"];
        DateTimeZone resolved = AvailabilityExpansion.ResolveZone(reloaded, DateTimeZoneProviders.Tzdb, ownerZone);
        Assert.Equal(ownerZone, resolved);

        TimeRange window = new(
            Instant.FromUtc(2026, 9, 1, 0, 0), Instant.FromUtc(2026, 9, 30, 0, 0));
        IReadOnlyList<AvailabilityOccurrence> occurrences = AvailabilityExpansion.Expand(
            reloaded, [], window, DateTimeZoneProviders.Tzdb, ownerZone);
        Assert.NotEmpty(occurrences);
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
