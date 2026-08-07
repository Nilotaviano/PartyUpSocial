using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Friends;
using PartyUp.Application.Deletion;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §11 "Commands (S3)": DOMAIN_MODEL.md §11 step 5 deletes the tombstoned owner's
/// whole availability surface — series, one-time blocks, materialized blocks and skips — through the
/// real grace-expiry executor (<c>ExecuteDeletionForUserUseCase</c>), not a standalone repository call.
/// Against real Postgres because <see cref="PartyUp.Infrastructure.Friends.DeletionResultApplier"/> and
/// <see cref="PartyUp.Infrastructure.Deletion.DeletionScopeProvider"/> are Infrastructure types
/// <c>PartyUp.Application.Tests</c> may not reference.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityDeletionEffectsTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GraceExpiryRemovesSeriesBlocksAndSkipsThroughTheExecutor_D092()
    {
        UserId subjectId = await _factory.SeedUserAsync(
            pendingDeletion: true, email: $"avail-delete-{Guid.NewGuid():N}@example.test");

        Guid seriesId;
        Guid standaloneBlockId;
        Guid materializedBlockId;

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AvailabilitySeries series = AvailabilitySeries.Create(
                subjectId,
                WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60)),
                DateTimeZoneProviders.Tzdb["America/Sao_Paulo"],
                new LocalDate(2026, 9, 1));
            seriesId = series.Id.Value;

            LocalDate materializedDate = new(2026, 9, 8);
            Instant materializedStart = materializedDate.AtStartOfDayInZone(
                DateTimeZoneProviders.Tzdb["America/Sao_Paulo"]).ToInstant();
            AvailabilityBlock materializedBlock = AvailabilityBlock.Materialize(
                series,
                materializedDate,
                new TimeRange(materializedStart, materializedStart + Duration.FromHours(1)));
            materializedBlockId = materializedBlock.Id.Value;

            AvailabilityOccurrenceSkip skip = AvailabilityOccurrenceSkip.For(series, materializedDate);

            Instant standaloneStart = Instant.FromUtc(2026, 9, 20, 10, 0);
            AvailabilityBlock standaloneBlock = AvailabilityBlock.Create(
                subjectId, new TimeRange(standaloneStart, standaloneStart + Duration.FromMinutes(30)));
            standaloneBlockId = standaloneBlock.Id.Value;

            context.AvailabilitySeries.Add(series);
            context.AvailabilityBlocks.Add(materializedBlock);
            context.AvailabilityBlocks.Add(standaloneBlock);
            context.AvailabilityOccurrenceSkips.Add(skip);
            await context.SaveChangesAsync();
        }

        _factory.Clock.Advance(Duration.FromDays(8));

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            ExecuteDeletionForUserUseCase useCase = Phase3DeletionTestSupport.CreateUseCase(scope.ServiceProvider);
            bool executed = await useCase.ExecuteAsync(subjectId, default);
            Assert.True(executed);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        AvailabilitySeriesId typedSeriesId = AvailabilitySeriesId.From(seriesId);
        AvailabilityBlockId typedMaterializedBlockId = AvailabilityBlockId.From(materializedBlockId);
        AvailabilityBlockId typedStandaloneBlockId = AvailabilityBlockId.From(standaloneBlockId);
        Assert.False(await verify.AvailabilitySeries.AsNoTracking().AnyAsync(s => s.Id == typedSeriesId));
        Assert.False(await verify.AvailabilityBlocks.AsNoTracking().AnyAsync(b => b.Id == typedMaterializedBlockId));
        Assert.False(await verify.AvailabilityBlocks.AsNoTracking().AnyAsync(b => b.Id == typedStandaloneBlockId));
        Assert.False(await verify.AvailabilityOccurrenceSkips.AsNoTracking().AnyAsync(s => s.SeriesId == typedSeriesId));
    }
}
