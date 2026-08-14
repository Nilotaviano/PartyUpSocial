using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// PHASE5_DESIGN.md §5.1/A-079: <c>ReadOwnersWindowAsync</c> issues exactly four SQL statements for
/// the whole owner set, never one per owner. This turns S4's two-port-call shape budget
/// (<c>AvailabilityLoadShapeTests</c>) into a five-round-trip ceiling.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityReadRepositoryStatementCountTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task ReadOwnersWindowIssuesExactlyFourStatementsRegardlessOfOwnerCount_A076_A079(int ownerCount)
    {
        await using AppDbContext seedContext = CreateContext(null);
        await seedContext.Database.MigrateAsync();

        List<UserId> ownerIds = [];
        for (int i = 0; i < ownerCount; i++)
        {
            UserId ownerId = await SeedUserAsync(seedContext);
            ownerIds.Add(ownerId);

            AvailabilitySeries series = AvailabilitySeries.Create(
                ownerId,
                WeeklyPattern.Create(IsoDayOfWeek.Tuesday, new LocalTime(18, 0), Duration15.FromMinutes(60)),
                DateTimeZoneProviders.Tzdb["America/Fortaleza"],
                new LocalDate(2026, 9, 1));
            seedContext.AvailabilitySeries.Add(series);

            Instant blockStart = Instant.FromUtc(2026, 9, 10, 14, 0);
            seedContext.AvailabilityBlocks.Add(AvailabilityBlock.Create(
                ownerId, new TimeRange(blockStart, blockStart + Duration.FromMinutes(30))));
        }

        await seedContext.SaveChangesAsync();

        var counter = new StatementCountingLogger();
        await using AppDbContext readContext = CreateContext(counter);
        var repository = new AvailabilityReadRepository(readContext);

        TimeRange window = new(
            Instant.FromUtc(2026, 9, 1, 0, 0), Instant.FromUtc(2026, 9, 30, 0, 0));
        IReadOnlyList<AvailabilityOwnerData> result = await repository.ReadOwnersWindowAsync(ownerIds, window, default);

        Assert.Equal(ownerCount, result.Count);
        Assert.Equal(4, counter.StatementCount);
    }

    [Fact]
    public async Task ReadOwnersWindowReturnsOwnersOrderedByOwnerIdRegardlessOfInsertOrRequestOrder()
    {
        await using AppDbContext seedContext = CreateContext(null);
        await seedContext.Database.MigrateAsync();

        List<UserId> ownerIds = [];
        for (int i = 0; i < 5; i++)
        {
            ownerIds.Add(await SeedUserAsync(seedContext));
        }

        await seedContext.SaveChangesAsync();

        // Request order is the reverse of insertion order — if the read ever fell back to a
        // storage-order or request-order shape, this would catch it; only an explicit ORDER BY on
        // the owner id survives both permutations.
        List<UserId> requestOrder = [.. ownerIds.AsEnumerable().Reverse()];
        List<Guid> expectedOrder = [.. ownerIds.Select(id => id.Value).OrderBy(value => value)];

        await using AppDbContext readContext = CreateContext(null);
        var repository = new AvailabilityReadRepository(readContext);

        TimeRange window = new(
            Instant.FromUtc(2026, 9, 1, 0, 0), Instant.FromUtc(2026, 9, 30, 0, 0));

        IReadOnlyList<AvailabilityOwnerData> firstRead =
            await repository.ReadOwnersWindowAsync(requestOrder, window, default);
        IReadOnlyList<AvailabilityOwnerData> secondRead =
            await repository.ReadOwnersWindowAsync(requestOrder, window, default);

        Assert.Equal(expectedOrder, [.. firstRead.Select(o => o.OwnerId.Value)]);
        Assert.Equal(expectedOrder, [.. secondRead.Select(o => o.OwnerId.Value)]);
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
            DisplayName.Create("Statement Count Owner"),
            Avatar.FromEmoji("🙂"),
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"),
            SystemClock.Instance);
        context.DomainUsers.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private AppDbContext CreateContext(StatementCountingLogger? counter)
    {
        DbContextOptionsBuilder<AppDbContext> builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention();

        if (counter is not null)
        {
            builder.UseLoggerFactory(new LoggerFactory([counter]));
        }

        return new AppDbContext(builder.Options);
    }

    /// <summary>
    /// Counts executed commands by observing EF's relational command-executed event, rather than
    /// counting rows or ports called — the A-079 budget is stated in round trips (§5.1).
    /// </summary>
    private sealed class StatementCountingLogger : ILoggerProvider, ILogger
    {
        private int _count;

        public int StatementCount => Volatile.Read(ref _count);

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted fires once per executed
            // ADO.NET command — exactly the "one statement" unit A-079's budget is stated in.
            if (eventId.Id == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted.Id)
            {
                Interlocked.Increment(ref _count);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
