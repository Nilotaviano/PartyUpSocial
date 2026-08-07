using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Availability;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §11 "Commands (S3)": a repeated <c>onlyThis</c> edit of the same occurrence date
/// reschedules the block the first edit produced rather than writing a second skip (§5.2). Against
/// real Postgres because the <c>availability_occurrence_skips</c> composite PK (§4.1) is what makes a
/// bug here observable — an in-memory fake enforces no such uniqueness.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityOccurrenceEditIdempotencyTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb["America/Sao_Paulo"];

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RepeatedOnlyThisEditUpdatesTheSameBlock_D143()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"occ-idem-{Guid.NewGuid():N}@example.test");
        Guid seriesId;
        LocalDate occurrenceDate = new(2026, 9, 8);

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AvailabilitySeries series = AvailabilitySeries.Create(
                ownerId,
                WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60)),
                Zone,
                new LocalDate(2026, 9, 1));
            seriesId = series.Id.Value;
            context.AvailabilitySeries.Add(series);
            await context.SaveChangesAsync();
        }

        TimeRange firstWindow = OnDate(occurrenceDate, new LocalTime(20, 0), new LocalTime(21, 0));
        Guid firstBlockId;
        await using (AsyncServiceScope firstScope = _factory.Services.CreateAsyncScope())
        {
            var useCase = firstScope.ServiceProvider.GetRequiredService<EditAvailabilityOccurrenceUseCase>();
            OccurrenceEditOutcome first = await useCase.ExecuteAsync(
                ownerId,
                AvailabilitySeriesId.From(seriesId),
                occurrenceDate,
                OccurrenceEditScope.OnlyThis,
                firstWindow,
                null,
                default);
            firstBlockId = first.Block!.Id.Value;
        }

        TimeRange secondWindow = OnDate(occurrenceDate, new LocalTime(21, 0), new LocalTime(22, 30));
        await using (AsyncServiceScope secondScope = _factory.Services.CreateAsyncScope())
        {
            var useCase = secondScope.ServiceProvider.GetRequiredService<EditAvailabilityOccurrenceUseCase>();
            OccurrenceEditOutcome second = await useCase.ExecuteAsync(
                ownerId,
                AvailabilitySeriesId.From(seriesId),
                occurrenceDate,
                OccurrenceEditScope.OnlyThis,
                secondWindow,
                null,
                default);
            Assert.Equal(firstBlockId, second.Block!.Id.Value);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        AvailabilitySeriesId typedSeriesId = AvailabilitySeriesId.From(seriesId);
        int skipCount = await verify.AvailabilityOccurrenceSkips.AsNoTracking()
            .CountAsync(s => s.SeriesId == typedSeriesId && s.OccurrenceDate == occurrenceDate);
        Assert.Equal(1, skipCount);

        AvailabilityBlock[] blocks = await verify.AvailabilityBlocks.AsNoTracking()
            .Where(b => b.SourceSeriesId == typedSeriesId)
            .ToArrayAsync();
        AvailabilityBlock onlyBlock = Assert.Single(blocks);
        Assert.Equal(firstBlockId, onlyBlock.Id.Value);
        Assert.Equal(secondWindow.Start, onlyBlock.Start);
        Assert.Equal(secondWindow.End, onlyBlock.End);
    }

    private static TimeRange OnDate(LocalDate date, LocalTime start, LocalTime end)
    {
        Instant startInstant = Zone.AtLeniently(date.At(start)).ToInstant();
        Instant endInstant = Zone.AtLeniently(date.At(end)).ToInstant();
        return new TimeRange(startInstant, endInstant);
    }
}
