using NodaTime;
using PartyUp.Application.Availability;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Tests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §11 "Commands (S3)": the D-134/D-143 occurrence edit dispatch — §5.2's three
/// <c>onlyThis</c> branches and the <c>thisAndFuture</c> split, composed entirely from shipped Phase 1
/// Domain members (A-076).
/// </summary>
public sealed class AvailabilityOccurrenceCommandTests
{
    private static readonly UserId OwnerId = UserId.New();
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb["America/Sao_Paulo"];
    private static readonly LocalDate EffectiveFrom = new(2026, 9, 1); // a Tuesday.

    [Fact]
    public async Task OnlyThisWritesASkipAndAMaterializedBlock_D134_D143()
    {
        (FakeAvailabilityRepository repo, TestUnitOfWork uow, AvailabilitySeries series) = SeedWeeklySeries();
        LocalDate occurrenceDate = new(2026, 9, 8);
        TimeRange replacement = OnDate(occurrenceDate, new LocalTime(20, 0), new LocalTime(21, 0));

        OccurrenceEditOutcome first = await new EditAvailabilityOccurrenceUseCase(repo, uow).ExecuteAsync(
            OwnerId, series.Id, occurrenceDate, OccurrenceEditScope.OnlyThis, replacement, null, default);

        Assert.Equal(OccurrenceEditScope.OnlyThis, first.Scope);
        Assert.NotNull(first.Block);
        Guid firstBlockId = first.Block!.Id.Value;
        AvailabilityOccurrenceSkip skip = Assert.Single(repo.Skips);
        Assert.Equal(series.Id, skip.SeriesId);
        Assert.Equal(occurrenceDate, skip.OccurrenceDate);
        AvailabilityBlock block = Assert.Single(repo.Blocks);
        Assert.Equal(series.Id, block.SourceSeriesId);
        Assert.Equal(occurrenceDate, block.SourceOccurrenceDate);

        // A repeated onlyThis edit of the same date reschedules the existing block in place: no
        // second skip, the same blockId (§5.2's second branch).
        TimeRange secondReplacement = OnDate(occurrenceDate, new LocalTime(21, 0), new LocalTime(22, 0));
        OccurrenceEditOutcome second = await new EditAvailabilityOccurrenceUseCase(repo, uow).ExecuteAsync(
            OwnerId, series.Id, occurrenceDate, OccurrenceEditScope.OnlyThis, secondReplacement, null, default);

        Assert.Equal(firstBlockId, second.Block!.Id.Value);
        Assert.Single(repo.Skips);
        Assert.Single(repo.Blocks);
        Assert.Equal(secondReplacement.Start, repo.Blocks[0].Start);
    }

    [Fact]
    public async Task OnlyThisAfterADeleteOneIsOccurrenceNotFound_D143()
    {
        (FakeAvailabilityRepository repo, TestUnitOfWork uow, AvailabilitySeries series) = SeedWeeklySeries();
        LocalDate occurrenceDate = new(2026, 9, 8);

        await new DeleteAvailabilityOccurrenceUseCase(repo, uow).ExecuteAsync(
            OwnerId, series.Id, occurrenceDate, default);
        Assert.Single(repo.Skips);
        Assert.Empty(repo.Blocks);

        TimeRange replacement = OnDate(occurrenceDate, new LocalTime(20, 0), new LocalTime(21, 0));
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            new EditAvailabilityOccurrenceUseCase(repo, uow).ExecuteAsync(
                OwnerId, series.Id, occurrenceDate, OccurrenceEditScope.OnlyThis, replacement, null, default));
        Assert.Equal(DomainError.NotFound, exception.Error);
    }

    [Fact]
    public async Task ThisAndFutureSplitsDayGranularAndDropsFutureExceptions_D143()
    {
        (FakeAvailabilityRepository repo, TestUnitOfWork uow, AvailabilitySeries series) = SeedWeeklySeries();
        LocalDate splitDate = new(2026, 9, 15);
        LocalDate beforeSplit = new(2026, 9, 8);
        LocalDate afterSplit = new(2026, 9, 22);

        AvailabilityOccurrenceSkip keptSkip = AvailabilityOccurrenceSkip.For(series, beforeSplit);
        AvailabilityOccurrenceSkip droppedSkip = AvailabilityOccurrenceSkip.For(series, afterSplit);
        repo.Skips.Add(keptSkip);
        repo.Skips.Add(droppedSkip);

        AvailabilityBlock keptBlock = AvailabilityBlock.Materialize(series, beforeSplit, OnDate(beforeSplit, new LocalTime(20, 0), new LocalTime(21, 0)));
        AvailabilityBlock droppedBlock = AvailabilityBlock.Materialize(series, afterSplit, OnDate(afterSplit, new LocalTime(20, 0), new LocalTime(21, 0)));
        repo.Blocks.Add(keptBlock);
        repo.Blocks.Add(droppedBlock);

        WeeklyPattern replacement = WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(20, 0), Duration15.FromMinutes(90));
        OccurrenceEditOutcome outcome = await new EditAvailabilityOccurrenceUseCase(repo, uow).ExecuteAsync(
            OwnerId, series.Id, splitDate, OccurrenceEditScope.ThisAndFuture, null, replacement, default);

        Assert.Equal(OccurrenceEditScope.ThisAndFuture, outcome.Scope);
        Assert.NotNull(outcome.Original);
        Assert.Equal(splitDate, outcome.Original!.EffectiveUntil);
        Assert.Equal(splitDate, outcome.Successor!.EffectiveFrom);

        AvailabilityOccurrenceSkip remainingSkip = Assert.Single(repo.Skips);
        Assert.Equal(beforeSplit, remainingSkip.OccurrenceDate);
        AvailabilityBlock remainingBlock = Assert.Single(repo.Blocks);
        Assert.Equal(beforeSplit, remainingBlock.SourceOccurrenceDate);
    }

    [Fact]
    public async Task ThisAndFutureMovingTheWeekdayStartsTheSuccessorOnTheFirstNewWeekdayDateAndLeavesEarlierOccurrencesIntact_D143()
    {
        (FakeAvailabilityRepository repo, TestUnitOfWork uow, AvailabilitySeries series) = SeedWeeklySeries();
        LocalDate splitDate = new(2026, 9, 15);
        LocalDate beforeSplit = new(2026, 9, 8);

        AvailabilityOccurrenceSkip earlierSkip = AvailabilityOccurrenceSkip.For(series, beforeSplit);
        repo.Skips.Add(earlierSkip);

        // dayOfWeek moves from Tuesday to Thursday — the successor's EffectiveFrom still lands on the
        // split date; AvailabilityExpansion (Phase 1, already tested) is what advances to the first
        // Thursday on or after it. This proves the use case passes the new weekday through unmodified.
        WeeklyPattern replacement = WeeklyPattern.Create(IsoDayOfWeek.Thursday, new LocalTime(18, 0), Duration15.FromMinutes(45));
        OccurrenceEditOutcome outcome = await new EditAvailabilityOccurrenceUseCase(repo, uow).ExecuteAsync(
            OwnerId, series.Id, splitDate, OccurrenceEditScope.ThisAndFuture, null, replacement, default);

        Assert.Equal(IsoDayOfWeek.Thursday, outcome.Successor!.DayOfWeek);
        Assert.Equal(splitDate, outcome.Successor.EffectiveFrom);
        Assert.Equal(IsoDayOfWeek.Tuesday, outcome.Original!.DayOfWeek);
        Assert.Equal(splitDate, outcome.Original.EffectiveUntil);

        // The earlier occurrence's skip is untouched by the weekday change.
        AvailabilityOccurrenceSkip remaining = Assert.Single(repo.Skips);
        Assert.Equal(beforeSplit, remaining.OccurrenceDate);
    }

    [Fact]
    public async Task SplitConsumingTheWholeOriginalDeletesIt_D143()
    {
        (FakeAvailabilityRepository repo, TestUnitOfWork uow, AvailabilitySeries series) = SeedWeeklySeries();
        LocalDate splitDate = EffectiveFrom; // splitting at the very first occurrence empties the original.

        WeeklyPattern replacement = WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(20, 0), Duration15.FromMinutes(90));
        OccurrenceEditOutcome outcome = await new EditAvailabilityOccurrenceUseCase(repo, uow).ExecuteAsync(
            OwnerId, series.Id, splitDate, OccurrenceEditScope.ThisAndFuture, null, replacement, default);

        Assert.Null(outcome.Original);
        Assert.NotNull(outcome.Successor);
        Assert.DoesNotContain(series, repo.Series);
        Assert.Contains(outcome.Successor, repo.Series);
    }

    private static (FakeAvailabilityRepository Repo, TestUnitOfWork Uow, AvailabilitySeries Series) SeedWeeklySeries()
    {
        FakeAvailabilityRepository repo = new();
        TestUnitOfWork uow = new();
        WeeklyPattern pattern = WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60));
        AvailabilitySeries series = AvailabilitySeries.Create(OwnerId, pattern, Zone, EffectiveFrom);
        repo.Series.Add(series);
        return (repo, uow, series);
    }

    private static TimeRange OnDate(LocalDate date, LocalTime start, LocalTime end)
    {
        Instant startInstant = Zone.AtLeniently(date.At(start)).ToInstant();
        Instant endInstant = Zone.AtLeniently(date.At(end)).ToInstant();
        return new TimeRange(startInstant, endInstant);
    }
}
