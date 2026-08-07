using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Profile;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md A-081/P5-ZONE-01: the D-061 migration is a step inside
/// <c>ChangeTimeZoneUseCase</c>'s existing transaction — series-only, blocks provably untouched,
/// atomic with the profile change. Against real Postgres: an in-memory fake cannot observe whether
/// the migration shares the profile mutation's transaction (§11).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityZoneMigrationTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();
    private static readonly DateTimeZone OldZone = DateTimeZoneProviders.Tzdb["America/Fortaleza"];
    private static readonly DateTimeZone NewZone = DateTimeZoneProviders.Tzdb["Europe/Lisbon"];

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task SeriesMoveToTheSameLocalClockTimeAndBlocksDoNot_D061()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"zone-migrate-{Guid.NewGuid():N}@example.test");
        Guid seriesId;
        Guid blockId;
        Instant blockStart;
        Instant blockEnd;

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AvailabilitySeries series = AvailabilitySeries.Create(
                ownerId,
                WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60)),
                OldZone,
                new LocalDate(2026, 9, 1));
            seriesId = series.Id.Value;

            blockStart = Instant.FromUtc(2026, 9, 20, 10, 0);
            blockEnd = blockStart + Duration.FromMinutes(30);
            AvailabilityBlock block = AvailabilityBlock.Create(ownerId, new TimeRange(blockStart, blockEnd));
            blockId = block.Id.Value;

            context.AvailabilitySeries.Add(series);
            context.AvailabilityBlocks.Add(block);
            await context.SaveChangesAsync();
        }

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var useCase = scope.ServiceProvider.GetRequiredService<ChangeTimeZoneUseCase>();
            ProfileMutationResult result = await useCase.ExecuteAsync(ownerId, NewZone.Id, default);
            Assert.True(result.Succeeded);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        AvailabilitySeriesId typedSeriesId = AvailabilitySeriesId.From(seriesId);
        AvailabilityBlockId typedBlockId = AvailabilityBlockId.From(blockId);
        AvailabilitySeries reloadedSeries = await verify.AvailabilitySeries.AsNoTracking()
            .SingleAsync(s => s.Id == typedSeriesId);
        Assert.Equal(NewZone.Id, reloadedSeries.ZoneId);
        Assert.Equal(new LocalTime(19, 0), reloadedSeries.StartTime);

        AvailabilityBlock reloadedBlock = await verify.AvailabilityBlocks.AsNoTracking()
            .SingleAsync(b => b.Id == typedBlockId);
        Assert.Equal(blockStart, reloadedBlock.Start);
        Assert.Equal(blockEnd, reloadedBlock.End);
    }

    [Fact]
    public async Task RollsBackWithTheProfileChange_D061()
    {
        // A PendingDeletion user is not Active: User.ChangeTimeZone throws after the availability
        // retarget statement has already executed inside the same transaction, proving the whole
        // write — not just the series UPDATE — rolls back together.
        UserId ownerId = await _factory.SeedUserAsync(
            pendingDeletion: true, email: $"zone-rollback-{Guid.NewGuid():N}@example.test");
        Guid seriesId;

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AvailabilitySeries series = AvailabilitySeries.Create(
                ownerId,
                WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60)),
                OldZone,
                new LocalDate(2026, 9, 1));
            seriesId = series.Id.Value;
            context.AvailabilitySeries.Add(series);
            await context.SaveChangesAsync();
        }

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var useCase = scope.ServiceProvider.GetRequiredService<ChangeTimeZoneUseCase>();
            await Assert.ThrowsAsync<PartyUp.Domain.Kernel.DomainException>(
                () => useCase.ExecuteAsync(ownerId, NewZone.Id, default));
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        AvailabilitySeriesId typedSeriesId = AvailabilitySeriesId.From(seriesId);
        AvailabilitySeries reloadedSeries = await verify.AvailabilitySeries.AsNoTracking()
            .SingleAsync(s => s.Id == typedSeriesId);
        Assert.Equal(OldZone.Id, reloadedSeries.ZoneId);
    }
}
