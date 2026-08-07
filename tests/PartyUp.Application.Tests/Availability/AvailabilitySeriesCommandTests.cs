using NodaTime;
using PartyUp.Application.Availability;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Tests.Availability;

/// <summary>PHASE5_DESIGN.md §11 "Commands (S3)": weekly series create/delete (D-025/D-133).</summary>
public sealed class AvailabilitySeriesCommandTests
{
    private static readonly UserId OwnerId = UserId.New();
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb["America/Sao_Paulo"];
    private static readonly LocalDate EffectiveFrom = new(2026, 9, 1);

    [Fact]
    public async Task WeeklySeriesRunsUntilDeleted_D133()
    {
        FakeAvailabilityRepository repo = new();
        TestUnitOfWork uow = new();
        WeeklyPattern pattern = WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(120));

        AvailabilitySeries series = await new CreateAvailabilitySeriesUseCase(repo, uow).ExecuteAsync(
            OwnerId, pattern, Zone, EffectiveFrom, effectiveUntil: null, default);

        Assert.Contains(series, repo.Series);
        Assert.Null(series.EffectiveUntil);
        Assert.Equal(["owner"], repo.LockCalls);

        // D-133: no series cap — creating a second series for the same owner is unrestricted.
        AvailabilitySeries second = await new CreateAvailabilitySeriesUseCase(repo, uow).ExecuteAsync(
            OwnerId, pattern, Zone, EffectiveFrom, effectiveUntil: null, default);
        Assert.Equal(2, repo.Series.Count(s => s.OwnerId == OwnerId));
        Assert.NotEqual(series.Id, second.Id);

        // Runs until deleted: still present, unbounded, until an explicit delete.
        bool deleted = await new DeleteAvailabilitySeriesUseCase(repo, uow).ExecuteAsync(OwnerId, series.Id, default);
        Assert.True(deleted);
        Assert.DoesNotContain(series, repo.Series);
        Assert.Contains(second, repo.Series);
    }

    [Fact]
    public async Task DeleteCascadesToSkipsAndMaterializedBlocks_D025()
    {
        FakeAvailabilityRepository repo = new();
        TestUnitOfWork uow = new();
        WeeklyPattern pattern = WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(120));
        AvailabilitySeries series = AvailabilitySeries.Create(OwnerId, pattern, Zone, EffectiveFrom);
        repo.Series.Add(series);

        LocalDate firstOccurrence = new(2026, 9, 1);
        LocalDate secondOccurrence = new(2026, 9, 8);
        AvailabilityOccurrenceSkip skip = AvailabilityOccurrenceSkip.For(series, firstOccurrence);
        repo.Skips.Add(skip);

        Instant materializedStart = firstOccurrence.PlusWeeks(1).AtStartOfDayInZone(Zone).ToInstant();
        AvailabilityBlock materializedBlock = AvailabilityBlock.Materialize(
            series, secondOccurrence, new TimeRange(materializedStart, materializedStart + Duration.FromHours(1)));
        repo.Blocks.Add(materializedBlock);

        bool deleted = await new DeleteAvailabilitySeriesUseCase(repo, uow).ExecuteAsync(OwnerId, series.Id, default);

        Assert.True(deleted);
        Assert.DoesNotContain(series, repo.Series);
        Assert.Empty(repo.Skips);
        Assert.Empty(repo.Blocks);
    }

    [Fact]
    public async Task Delete_UnknownOrNonOwnerTargetIsIdempotentNoOp_D009()
    {
        FakeAvailabilityRepository repo = new();
        TestUnitOfWork uow = new();

        bool deletedMissing = await new DeleteAvailabilitySeriesUseCase(repo, uow).ExecuteAsync(
            OwnerId, AvailabilitySeriesId.New(), default);
        Assert.False(deletedMissing);

        WeeklyPattern pattern = WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60));
        AvailabilitySeries foreign = AvailabilitySeries.Create(UserId.New(), pattern, Zone, EffectiveFrom);
        repo.Series.Add(foreign);

        bool deletedForeign = await new DeleteAvailabilitySeriesUseCase(repo, uow).ExecuteAsync(OwnerId, foreign.Id, default);
        Assert.False(deletedForeign);
        Assert.Contains(foreign, repo.Series);
    }
}
