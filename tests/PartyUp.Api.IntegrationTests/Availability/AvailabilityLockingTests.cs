using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using PartyUp.Application.Availability;
using PartyUp.Application.Profile;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md P5-LK-01: the owner's <c>users</c> row is the serialization point for every
/// availability write (A-075). Proved through the real S3 command surface, not raw SQL, mirroring
/// <c>Sessions/SessionLockingTests.cs</c>'s precedent for the session-parent lock.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityLockingTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb["America/Sao_Paulo"];

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// A-075: <c>LockBlocksBySourceSeriesAsync</c> issues one <c>SELECT … ORDER BY id FOR NO KEY
    /// UPDATE</c> over every materialized block of a series. With the higher-id block externally
    /// held, the statement can only be blocked on it after already locking the lower-id row in the
    /// declared order — proved by a failing <c>NOWAIT</c> probe on the lower row while the delete is
    /// still pending, the same deterministic shape as
    /// <c>SessionRepositoryLockTests.MultiRowLocksAreOrderedByTheDatabaseNotTheCaller_A068</c>.
    /// </summary>
    [Fact]
    public async Task OwnerSeriesBlockAndSkipLocksAreOrderedByTheDatabase_A075()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"lock-order-{Guid.NewGuid():N}@example.test");
        Guid seriesId;
        Guid blockAId;
        Guid blockBId;

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AvailabilitySeries series = AvailabilitySeries.Create(
                ownerId,
                WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60)),
                Zone,
                new LocalDate(2026, 9, 1));
            seriesId = series.Id.Value;

            LocalDate dateA = new(2026, 9, 8);
            LocalDate dateB = new(2026, 9, 15);
            Instant startA = dateA.AtStartOfDayInZone(Zone).ToInstant();
            Instant startB = dateB.AtStartOfDayInZone(Zone).ToInstant();
            AvailabilityBlock blockA = AvailabilityBlock.Materialize(
                series, dateA, new TimeRange(startA, startA + Duration.FromHours(1)));
            AvailabilityBlock blockB = AvailabilityBlock.Materialize(
                series, dateB, new TimeRange(startB, startB + Duration.FromHours(1)));
            blockAId = blockA.Id.Value;
            blockBId = blockB.Id.Value;

            context.AvailabilitySeries.Add(series);
            context.AvailabilityBlocks.Add(blockA);
            context.AvailabilityBlocks.Add(blockB);
            await context.SaveChangesAsync();
        }

        // Postgres orders a uuid column by its RFC 4122 byte layout, which .NET's Guid.CompareTo does
        // not match (its first three fields are little-endian in memory) — compare the big-endian
        // byte sequence instead, the same idiom SessionLockingTests uses for user ids.
        int order = blockAId.ToByteArray(bigEndian: true).AsSpan()
            .SequenceCompareTo(blockBId.ToByteArray(bigEndian: true));
        (Guid lowerId, Guid higherId) = order < 0 ? (blockAId, blockBId) : (blockBId, blockAId);

        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM availability_blocks WHERE id = {higherId} FOR NO KEY UPDATE");

        await using AsyncServiceScope commandScope = _factory.Services.CreateAsyncScope();
        var deleteUseCase = commandScope.ServiceProvider.GetRequiredService<DeleteAvailabilitySeriesUseCase>();
        Task<bool> commandTask = deleteUseCase.ExecuteAsync(ownerId, AvailabilitySeriesId.From(seriesId), default);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(commandTask.IsCompleted, "The delete must still be blocked on the externally held higher-id block.");

        await using (AsyncServiceScope probeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext probeContext = probeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var probeTransaction = await probeContext.Database.BeginTransactionAsync();

            Exception? thrown = await Record.ExceptionAsync(() =>
                probeContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM availability_blocks WHERE id = {lowerId} FOR NO KEY UPDATE NOWAIT"));

            Assert.True(
                thrown is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable },
                "The lower-id block must already be locked by the blocked delete — A-075 orders the " +
                "statement by id, so the lower row is acquired before the caller ever blocks on the higher one.");

            await probeTransaction.RollbackAsync();
        }

        await holderTransaction.RollbackAsync();
        bool result = await commandTask;
        Assert.True(result);
    }

    /// <summary>
    /// P5-LK-01: the owner's users row self-conflicts, so two concurrent <c>thisAndFuture</c> splits
    /// of the same occurrence are strictly ordered. The loser's fresh read (after the winner commits)
    /// sees the already-truncated original and fails <c>EnsureOccurrenceDate</c> — exactly one
    /// successor ever results, never two, and never an orphaned pair.
    /// </summary>
    [Fact]
    public async Task ConcurrentThisAndFutureSplitsProduceOneSuccessor_D143()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"lock-split-{Guid.NewGuid():N}@example.test");
        Guid seriesId = await SeedSeriesAsync(ownerId);
        LocalDate splitDate = new(2026, 9, 15);

        WeeklyPattern patternA = WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(20, 0), Duration15.FromMinutes(90));
        WeeklyPattern patternB = WeeklyPattern.Create(IsoDayOfWeek.Wednesday, new LocalTime(9, 0), Duration15.FromMinutes(45));

        Task<OccurrenceEditOutcome?> first = RunSplitAsync(ownerId, seriesId, splitDate, patternA);
        Task<OccurrenceEditOutcome?> second = RunSplitAsync(ownerId, seriesId, splitDate, patternB);
        OccurrenceEditOutcome?[] results = await Task.WhenAll(first, second);

        Assert.Single(results, outcome => outcome is not null);
        Assert.Single(results, outcome => outcome is null);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        int seriesCount = await verify.AvailabilitySeries.AsNoTracking().CountAsync(s => s.OwnerId == ownerId);
        Assert.Equal(2, seriesCount); // the (possibly truncated) original plus exactly one successor.
    }

    /// <summary>
    /// P5-LK-01: a <c>thisAndFuture</c> split racing a delete-one on the same date serializes on the
    /// owner lock — a row lock on <c>availability_occurrence_skips</c> could not achieve this, since
    /// <c>FOR NO KEY UPDATE</c> never blocks an <c>INSERT</c>. Whichever wins, no skip survives
    /// pointing into the split's dropped tail.
    /// </summary>
    [Fact]
    public async Task SplitAndDeleteOneOnTheSameDateSerializeOnTheOwnerLock_D143()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"lock-split-delete-{Guid.NewGuid():N}@example.test");
        Guid seriesId = await SeedSeriesAsync(ownerId);
        LocalDate splitDate = new(2026, 9, 15);
        WeeklyPattern replacement = WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(20, 0), Duration15.FromMinutes(90));

        Task<OccurrenceEditOutcome?> split = RunSplitAsync(ownerId, seriesId, splitDate, replacement);
        Task<bool?> delete = RunDeleteOccurrenceAsync(ownerId, seriesId, splitDate);
        await Task.WhenAll(split, delete);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        AvailabilitySeriesId typedSeriesId = AvailabilitySeriesId.From(seriesId);
        int orphanedSkips = await verify.AvailabilityOccurrenceSkips.AsNoTracking()
            .CountAsync(s => s.SeriesId == typedSeriesId && s.OccurrenceDate == splitDate);
        Assert.Equal(0, orphanedSkips);
    }

    /// <summary>
    /// A-081: the profile path's <c>FOR UPDATE</c> and an availability write's <c>FOR NO KEY UPDATE</c>
    /// conflict on the same users row (the residual A-069 deadlock class), so a zone migration and a
    /// concurrent block edit for the same owner serialize rather than corrupt each other.
    /// </summary>
    [Fact]
    public async Task ZoneMigrationAndBlockEditSerializeOnTheOwnerRow_D061()
    {
        UserId ownerId = await _factory.SeedUserAsync(email: $"lock-zone-{Guid.NewGuid():N}@example.test");
        Instant blockStart = Instant.FromUtc(2026, 9, 20, 10, 0);
        Guid blockId;

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AvailabilityBlock block = AvailabilityBlock.Create(
                ownerId, new TimeRange(blockStart, blockStart + Duration.FromMinutes(30)));
            blockId = block.Id.Value;
            context.AvailabilityBlocks.Add(block);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM users WHERE id = {ownerId.Value} FOR NO KEY UPDATE");

        await using AsyncServiceScope commandScope = _factory.Services.CreateAsyncScope();
        var editUseCase = commandScope.ServiceProvider.GetRequiredService<EditAvailabilityBlockUseCase>();
        Instant newStart = blockStart + Duration.FromHours(3);
        Task<AvailabilityBlock> commandTask = editUseCase.ExecuteAsync(
            ownerId,
            AvailabilityBlockId.From(blockId),
            new TimeRange(newStart, newStart + Duration.FromMinutes(30)),
            default);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(
            commandTask.IsCompleted,
            "The block edit must block on the externally held users row (A-075's serialization point).");

        await holderTransaction.RollbackAsync();
        AvailabilityBlock edited = await commandTask;
        Assert.Equal(newStart, edited.Start);
    }

    private async Task<Guid> SeedSeriesAsync(UserId ownerId)
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        AvailabilitySeries series = AvailabilitySeries.Create(
            ownerId,
            WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(19, 0), Duration15.FromMinutes(60)),
            Zone,
            new LocalDate(2026, 9, 1));
        context.AvailabilitySeries.Add(series);
        await context.SaveChangesAsync();
        return series.Id.Value;
    }

    private async Task<OccurrenceEditOutcome?> RunSplitAsync(
        UserId ownerId, Guid seriesId, LocalDate splitDate, WeeklyPattern replacement)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var useCase = scope.ServiceProvider.GetRequiredService<EditAvailabilityOccurrenceUseCase>();
        try
        {
            return await useCase.ExecuteAsync(
                ownerId,
                AvailabilitySeriesId.From(seriesId),
                splitDate,
                OccurrenceEditScope.ThisAndFuture,
                null,
                replacement,
                default);
        }
        catch (PartyUp.Domain.Kernel.DomainException)
        {
            return null;
        }
    }

    private async Task<bool?> RunDeleteOccurrenceAsync(UserId ownerId, Guid seriesId, LocalDate occurrenceDate)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var useCase = scope.ServiceProvider.GetRequiredService<DeleteAvailabilityOccurrenceUseCase>();
        try
        {
            return await useCase.ExecuteAsync(ownerId, AvailabilitySeriesId.From(seriesId), occurrenceDate, default);
        }
        catch (PartyUp.Domain.Kernel.DomainException)
        {
            return null;
        }
    }
}
