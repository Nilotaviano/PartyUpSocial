using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Availability;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Tests.Availability;

/// <summary>
/// PHASE5_DESIGN.md §11 "Commands (S3)": one-time block commands are thin transaction wrappers over
/// Phase 1 Domain (A-076) — the grid/range check is <see cref="AvailabilityBlock"/>'s own constructor,
/// never re-expressed here.
/// </summary>
public sealed class AvailabilityBlockCommandTests
{
    private static readonly UserId OwnerId = UserId.New();
    private static readonly Instant Base = Instant.FromUtc(2026, 9, 1, 9, 0);

    [Fact]
    public async Task CreateEditDeleteStayOnTheFifteenMinuteGrid_D071_D132()
    {
        FakeAvailabilityRepository repo = new();
        TestUnitOfWork uow = new();

        // Create: a valid on-grid range succeeds and locks only the owner row (§6.1 step 4 omitted).
        AvailabilityBlock created = await new CreateAvailabilityBlockUseCase(repo, uow).ExecuteAsync(
            OwnerId, new TimeRange(Base, Base + Duration.FromMinutes(30)), default);
        Assert.Contains(created, repo.Blocks);
        Assert.Equal(["owner"], repo.LockCalls);

        // Create: an off-grid range is rejected by the Domain constructor, before any row is touched.
        repo.LockCalls.Clear();
        await Assert.ThrowsAsync<DomainException>(() => new CreateAvailabilityBlockUseCase(repo, uow).ExecuteAsync(
            OwnerId, new TimeRange(Base + Duration.FromMinutes(7), Base + Duration.FromMinutes(37)), default));

        // Edit: rescheduling to a valid on-grid range succeeds.
        AvailabilityBlock edited = await new EditAvailabilityBlockUseCase(repo, uow).ExecuteAsync(
            OwnerId, created.Id, new TimeRange(Base + Duration.FromHours(1), Base + Duration.FromHours(2)), default);
        Assert.Equal(created.Id, edited.Id);
        Assert.Equal(Base + Duration.FromHours(1), edited.Start);

        // Edit: an off-grid replacement is rejected.
        await Assert.ThrowsAsync<DomainException>(() => new EditAvailabilityBlockUseCase(repo, uow).ExecuteAsync(
            OwnerId, created.Id, new TimeRange(Base + Duration.FromMinutes(5), Base + Duration.FromMinutes(20)), default));

        // Delete: succeeds and removes the row.
        bool deleted = await new DeleteAvailabilityBlockUseCase(repo, uow).ExecuteAsync(OwnerId, created.Id, default);
        Assert.True(deleted);
        Assert.DoesNotContain(created, repo.Blocks);

        // Delete: idempotent — a second delete of the same (now-missing) id is a no-op, not an error.
        bool secondDelete = await new DeleteAvailabilityBlockUseCase(repo, uow).ExecuteAsync(OwnerId, created.Id, default);
        Assert.False(secondDelete);
    }

    [Fact]
    public async Task Edit_NonOwnerTargetIsNotFound_D009()
    {
        FakeAvailabilityRepository repo = new();
        TestUnitOfWork uow = new();
        AvailabilityBlock foreign = AvailabilityBlock.Create(
            UserId.New(), new TimeRange(Base, Base + Duration.FromMinutes(30)));
        repo.Blocks.Add(foreign);

        await Assert.ThrowsAsync<DomainException>(() => new EditAvailabilityBlockUseCase(repo, uow).ExecuteAsync(
            OwnerId, foreign.Id, new TimeRange(Base + Duration.FromHours(1), Base + Duration.FromHours(2)), default));
    }
}

/// <summary>Shared in-memory fakes for the S3 Application command tests.</summary>
internal sealed class FakeAvailabilityRepository : IAvailabilityRepository
{
    internal List<AvailabilitySeries> Series { get; } = [];

    internal List<AvailabilityBlock> Blocks { get; } = [];

    internal List<AvailabilityOccurrenceSkip> Skips { get; } = [];

    internal List<string> LockCalls { get; } = [];

    public Task LockOwnerAsync(UserId ownerId, CancellationToken cancellationToken)
    {
        LockCalls.Add("owner");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AvailabilitySeries>> LockSeriesAsync(
        IReadOnlyCollection<AvailabilitySeriesId> seriesIds, CancellationToken cancellationToken)
    {
        LockCalls.Add("series");
        return Task.FromResult<IReadOnlyList<AvailabilitySeries>>(
            [.. Series.Where(s => seriesIds.Contains(s.Id))]);
    }

    public Task<IReadOnlyList<AvailabilitySeries>> LockOwnerSeriesAsync(
        UserId ownerId, CancellationToken cancellationToken)
    {
        LockCalls.Add("owner-series");
        return Task.FromResult<IReadOnlyList<AvailabilitySeries>>(
            [.. Series.Where(s => s.OwnerId == ownerId)]);
    }

    public Task<IReadOnlyList<AvailabilityBlock>> LockBlocksAsync(
        IReadOnlyCollection<AvailabilityBlockId> blockIds, CancellationToken cancellationToken)
    {
        LockCalls.Add("blocks");
        return Task.FromResult<IReadOnlyList<AvailabilityBlock>>(
            [.. Blocks.Where(b => blockIds.Contains(b.Id))]);
    }

    public Task<IReadOnlyList<AvailabilityBlock>> LockBlocksBySourceSeriesAsync(
        IReadOnlyCollection<AvailabilitySeriesId> seriesIds, CancellationToken cancellationToken)
    {
        LockCalls.Add("blocks-by-series");
        return Task.FromResult<IReadOnlyList<AvailabilityBlock>>(
            [.. Blocks.Where(b => b.SourceSeriesId is { } sourceId && seriesIds.Contains(sourceId))]);
    }

    public Task<IReadOnlyList<AvailabilityBlock>> LockBlocksByOwnerAsync(
        UserId ownerId, CancellationToken cancellationToken)
    {
        LockCalls.Add("blocks-by-owner");
        return Task.FromResult<IReadOnlyList<AvailabilityBlock>>(
            [.. Blocks.Where(b => b.OwnerId == ownerId)]);
    }

    public Task<IReadOnlyList<AvailabilityOccurrenceSkip>> LockSkipsAsync(
        IReadOnlyCollection<AvailabilitySeriesId> seriesIds, CancellationToken cancellationToken)
    {
        LockCalls.Add("skips");
        return Task.FromResult<IReadOnlyList<AvailabilityOccurrenceSkip>>(
            [.. Skips.Where(s => seriesIds.Contains(s.SeriesId))]);
    }

    public AvailabilityOccurrenceSkip? FindSkip(
        IReadOnlyList<AvailabilityOccurrenceSkip> lockedSkips, AvailabilitySeriesId seriesId, LocalDate occurrenceDate) =>
        lockedSkips.FirstOrDefault(skip => skip.SeriesId == seriesId && skip.OccurrenceDate == occurrenceDate);

    public AvailabilityBlock? FindMaterializedBlock(
        IReadOnlyList<AvailabilityBlock> lockedBlocks, AvailabilitySeriesId seriesId, LocalDate occurrenceDate) =>
        lockedBlocks.FirstOrDefault(block =>
            block.SourceSeriesId == seriesId && block.SourceOccurrenceDate == occurrenceDate);

    public void AddBlock(AvailabilityBlock block) => Blocks.Add(block);

    public void RemoveBlock(AvailabilityBlock block) => Blocks.RemoveAll(b => b.Id == block.Id);

    public void AddSeries(AvailabilitySeries series) => Series.Add(series);

    public void RemoveSeries(AvailabilitySeries series) => Series.RemoveAll(s => s.Id == series.Id);

    public void AddSkip(AvailabilityOccurrenceSkip skip) => Skips.Add(skip);

    public void RemoveSkipsFrom(AvailabilitySeriesId seriesId, LocalDate from) =>
        Skips.RemoveAll(skip => skip.SeriesId == seriesId && skip.OccurrenceDate >= from);

    public void RemoveMaterializedBlocksFrom(AvailabilitySeriesId seriesId, LocalDate from) =>
        Blocks.RemoveAll(block =>
            block.SourceSeriesId == seriesId && block.SourceOccurrenceDate is { } date && date >= from);

    public Task RetargetSeriesZoneAsync(UserId ownerId, string zoneId, CancellationToken cancellationToken)
    {
        DateTimeZone zone = DateTimeZoneProviders.Tzdb[zoneId];
        foreach (AvailabilitySeries series in Series.Where(s => s.OwnerId == ownerId))
        {
            series.ChangeZone(zone);
        }

        return Task.CompletedTask;
    }
}

internal sealed class TestUnitOfWork : IUnitOfWork
{
    internal int Executions { get; private set; }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        Executions++;
        return await work(cancellationToken);
    }
}
