using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// F1: the A-072 keyset list methods over the value-converted typed <c>id</c> column. Every method
/// used to throw ("could not be translated") because it ordered/compared via
/// <c>session.Id.Value.CompareTo(...)</c>, which EF cannot translate. This proves the fixed,
/// raw-SQL-ordered implementation actually runs against real Postgres: a first page, a cursored
/// continuation, tie-break stability for rows sharing one sort instant, and history's descending
/// order.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionReadRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly Instant _baseInstant = Instant.FromUtc(2026, 9, 1, 0, 0, 0);

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task ListUpcomingAsync_PagesByStartTimeThenIdWithStableTieBreak_A072()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId organizerId = await SeedUserAsync(context);
        var repository = new SessionReadRepository(context);

        // Five distinct start times, ascending.
        List<Guid> distinct = [];
        for (int i = 0; i < 5; i++)
        {
            Guid id = Guid.CreateVersion7();
            await InsertSessionAsync(context, organizerId, id, _baseInstant + Duration.FromHours(i), status: 0);
            distinct.Add(id);
        }

        // One active Going row on the first session: occupancy must survive hydration (a missing
        // Include leaves every summary at zero).
        await InsertParticipationAsync(context, Guid.CreateVersion7(), distinct[0], organizerId);

        SessionPage<SessionSummary> firstPage = await repository.ListUpcomingAsync(organizerId, null, 3, default);
        Assert.Equal(3, firstPage.Items.Count);
        Assert.Equal(1, firstPage.Items[0].ActiveOccupancy);
        Assert.Equal(0, firstPage.Items[1].ActiveOccupancy);
        Assert.Equal(distinct[0], firstPage.Items[0].Id.Value);
        Assert.Equal(distinct[1], firstPage.Items[1].Id.Value);
        Assert.Equal(distinct[2], firstPage.Items[2].Id.Value);
        Assert.NotNull(firstPage.NextCursor);

        SessionListCursor? cursor = SessionListCursor.TryDecode(firstPage.NextCursor);
        Assert.NotNull(cursor);

        SessionPage<SessionSummary> secondPage = await repository.ListUpcomingAsync(organizerId, cursor, 3, default);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Equal(distinct[3], secondPage.Items[0].Id.Value);
        Assert.Equal(distinct[4], secondPage.Items[1].Id.Value);
        Assert.Null(secondPage.NextCursor);

        // Three more rows sharing one identical sort instant, later than every row above so this page
        // isolates them: the repository's tie-break order must match Postgres's own `ORDER BY id` —
        // proving the tie-break is uuid order, not insertion order or a translation failure.
        Instant tieInstant = _baseInstant + Duration.FromDays(1);
        List<Guid> tied = [];
        for (int i = 0; i < 3; i++)
        {
            Guid id = Guid.CreateVersion7();
            await InsertSessionAsync(context, organizerId, id, tieInstant, status: 0);
            tied.Add(id);
        }

        List<Guid> expectedTieOrder = await QueryIdsOrderedAsync(
            context,
            "SELECT id FROM sessions WHERE start_time = @tie ORDER BY id",
            tieInstant);

        // A cursor pointing just past the last of the five distinct-start-time rows isolates the
        // three tied rows that follow it.
        SessionListCursor tieCursor = new(_baseInstant + Duration.FromHours(4), distinct[4]);
        SessionPage<SessionSummary> tiePage = await repository.ListUpcomingAsync(organizerId, tieCursor, 10, default);

        Assert.Equal(3, tiePage.Items.Count);
        Assert.Equal(expectedTieOrder, [.. tiePage.Items.Select(item => item.Id.Value)]);
    }

    [Fact]
    public async Task ListHistoryAsync_PagesByStartTimeDescendingThenId_A072()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId organizerId = await SeedUserAsync(context);
        var repository = new SessionReadRepository(context);

        // Completed sessions, ascending insertion but descending expectation.
        List<Guid> ascendingByStart = [];
        for (int i = 0; i < 5; i++)
        {
            Guid id = Guid.CreateVersion7();
            await InsertSessionAsync(
                context,
                organizerId,
                id,
                _baseInstant - Duration.FromDays(10) + Duration.FromHours(i),
                status: 2);
            ascendingByStart.Add(id);
        }

        List<Guid> expectedDescending = [.. ascendingByStart.AsEnumerable().Reverse()];

        SessionPage<SessionSummary> firstPage = await repository.ListHistoryAsync(organizerId, null, 3, default);
        Assert.Equal(3, firstPage.Items.Count);
        Assert.Equal(expectedDescending[0], firstPage.Items[0].Id.Value);
        Assert.Equal(expectedDescending[1], firstPage.Items[1].Id.Value);
        Assert.Equal(expectedDescending[2], firstPage.Items[2].Id.Value);
        Assert.NotNull(firstPage.NextCursor);

        SessionListCursor? cursor = SessionListCursor.TryDecode(firstPage.NextCursor);
        Assert.NotNull(cursor);

        SessionPage<SessionSummary> secondPage = await repository.ListHistoryAsync(organizerId, cursor, 3, default);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Equal(expectedDescending[3], secondPage.Items[0].Id.Value);
        Assert.Equal(expectedDescending[4], secondPage.Items[1].Id.Value);
        Assert.Null(secondPage.NextCursor);

        // D-221: Unconfirmed and Expired never surface; Canceled now does, carrying its own status.
        await InsertSessionAsync(context, organizerId, Guid.CreateVersion7(), _baseInstant, status: 0);
        await InsertSessionAsync(context, organizerId, Guid.CreateVersion7(), _baseInstant, status: 3);
        Guid canceledId = Guid.CreateVersion7();
        await InsertSessionAsync(context, organizerId, canceledId, _baseInstant, status: 4);

        SessionPage<SessionSummary> everything = await repository.ListHistoryAsync(organizerId, null, 20, default);
        Assert.Equal(6, everything.Items.Count);
        Assert.Equal(
            SessionStatus.Canceled,
            everything.Items.Single(item => item.Id.Value == canceledId).Status);
    }

    /// <summary>
    /// D-221: history is what the viewer took part in. The organizer arm and the active-Going arm
    /// both qualify; a Declined row, a Pending invitation, a waitlisted Going row and pure inherited
    /// visibility do not — even though every one of those sessions resolves visible to the viewer.
    /// </summary>
    [Fact]
    public async Task ListHistoryAsync_HoldsOnlySessionsTheViewerTookPartIn_D221()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId organizerId = await SeedUserAsync(context);
        UserId viewerId = await SeedUserAsync(context);
        var repository = new SessionReadRepository(context);

        Guid organized = Guid.CreateVersion7();
        await InsertSessionAsync(context, viewerId, organized, _baseInstant - Duration.FromDays(9), status: 2);

        Guid attended = Guid.CreateVersion7();
        await InsertSessionAsync(context, organizerId, attended, _baseInstant - Duration.FromDays(8), status: 2);
        await InsertParticipationAsync(context, Guid.CreateVersion7(), attended, viewerId);

        Guid declined = Guid.CreateVersion7();
        await InsertSessionAsync(context, organizerId, declined, _baseInstant - Duration.FromDays(7), status: 2);
        await InsertParticipationAsync(context, Guid.CreateVersion7(), declined, viewerId, status: 3);

        Guid invited = Guid.CreateVersion7();
        await InsertSessionAsync(context, organizerId, invited, _baseInstant - Duration.FromDays(6), status: 2);
        await InsertParticipationAsync(context, Guid.CreateVersion7(), invited, viewerId, status: 0);

        Guid waitlisted = Guid.CreateVersion7();
        await InsertSessionAsync(context, organizerId, waitlisted, _baseInstant - Duration.FromDays(5), status: 2);
        await InsertParticipationAsync(
            context, Guid.CreateVersion7(), waitlisted, viewerId, isWaitlisted: true);

        // Public and organized by someone the viewer is not attached to at all: visible under rule 2
        // for anyone whose friends are on it, never part of the viewer's own history.
        Guid strangers = Guid.CreateVersion7();
        await InsertSessionAsync(context, organizerId, strangers, _baseInstant - Duration.FromDays(4), status: 2);

        SessionPage<SessionSummary> history = await repository.ListHistoryAsync(viewerId, null, 20, default);

        Assert.Equal([attended, organized], history.Items.Select(item => item.Id.Value));
    }

    /// <summary>
    /// D-221: each history item carries the organizer plus every active Going registered participant,
    /// organizer first. The repository resolves the nameplates without judging who may read them —
    /// that masking is <c>SessionReadUseCases</c>'s, proven by its own unit tests.
    /// </summary>
    [Fact]
    public async Task ListHistoryAsync_CarriesTheGoingRosterOrganizerFirst_D221()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId organizerId = await SeedUserAsync(context);
        UserId goingId = await SeedUserAsync(context);
        UserId maybeId = await SeedUserAsync(context);
        var repository = new SessionReadRepository(context);

        Guid sessionId = Guid.CreateVersion7();
        await InsertSessionAsync(context, organizerId, sessionId, _baseInstant - Duration.FromDays(3), status: 2);
        await InsertParticipationAsync(context, Guid.CreateVersion7(), sessionId, organizerId);
        await InsertParticipationAsync(context, Guid.CreateVersion7(), sessionId, goingId);
        await InsertParticipationAsync(context, Guid.CreateVersion7(), sessionId, maybeId, status: 2);

        SessionPage<SessionSummary> history = await repository.ListHistoryAsync(organizerId, null, 20, default);

        IReadOnlyList<HistoryPlayer> players = Assert.Single(history.Items).Players!;
        Assert.Equal([organizerId, goingId], players.Select(player => player.UserId));
    }

    /// <summary>
    /// D-222: the recap's facts come from the Completed arm alone — the Canceled session the D-221
    /// list also shows contributes neither minutes nor companions.
    /// </summary>
    [Fact]
    public async Task ListCompletedHistoryFactsAsync_ExcludesCanceledAndTheViewerThemselves_D222()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId organizerId = await SeedUserAsync(context);
        UserId mateId = await SeedUserAsync(context);
        var repository = new SessionReadRepository(context);

        Guid completed = Guid.CreateVersion7();
        await InsertSessionAsync(context, organizerId, completed, _baseInstant - Duration.FromDays(3), status: 2);
        await InsertParticipationAsync(context, Guid.CreateVersion7(), completed, organizerId);
        await InsertParticipationAsync(context, Guid.CreateVersion7(), completed, mateId);

        Guid canceled = Guid.CreateVersion7();
        await InsertSessionAsync(context, organizerId, canceled, _baseInstant - Duration.FromDays(2), status: 4);

        IReadOnlyList<CompletedSessionFact> facts =
            await repository.ListCompletedHistoryFactsAsync(organizerId, default);

        CompletedSessionFact fact = Assert.Single(facts);
        Assert.Equal(completed, fact.SessionId.Value);
        Assert.Equal([mateId], fact.Companions);
    }

    [Fact]
    public async Task ListJoinRequestsAsync_PagesByCreatedAtThenIdWithStableTieBreak_A072()
    {
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
        UserId organizerId = await SeedUserAsync(context);
        Guid sessionId = Guid.CreateVersion7();
        await InsertSessionAsync(context, organizerId, sessionId, _baseInstant, status: 0);
        var repository = new SessionReadRepository(context);

        List<Guid> ordered = [];
        for (int i = 0; i < 4; i++)
        {
            UserId requesterId = await SeedUserAsync(context);
            Guid requestId = Guid.CreateVersion7();
            await InsertJoinRequestAsync(context, requestId, sessionId, requesterId, _baseInstant + Duration.FromMinutes(i));
            ordered.Add(requestId);
        }

        SessionPage<JoinRequestSummary> firstPage =
            await repository.ListJoinRequestsAsync(SessionId.From(sessionId), null, 3, default);
        Assert.Equal(3, firstPage.Items.Count);
        Assert.Equal(ordered[0], firstPage.Items[0].Id.Value);
        Assert.Equal(ordered[1], firstPage.Items[1].Id.Value);
        Assert.Equal(ordered[2], firstPage.Items[2].Id.Value);
        Assert.NotNull(firstPage.NextCursor);

        SessionListCursor? cursor = SessionListCursor.TryDecode(firstPage.NextCursor);
        Assert.NotNull(cursor);

        SessionPage<JoinRequestSummary> secondPage =
            await repository.ListJoinRequestsAsync(SessionId.From(sessionId), cursor, 3, default);
        Assert.Single(secondPage.Items);
        Assert.Equal(ordered[3], secondPage.Items[0].Id.Value);
        Assert.Null(secondPage.NextCursor);

        // Three more requests sharing one identical `created_at` — the tie-break must match
        // Postgres's own `ORDER BY id`, proving uuid-order stability rather than insertion order.
        Instant tieInstant = _baseInstant + Duration.FromHours(1);
        List<Guid> tied = [];
        for (int i = 0; i < 3; i++)
        {
            UserId requesterId = await SeedUserAsync(context);
            Guid requestId = Guid.CreateVersion7();
            await InsertJoinRequestAsync(context, requestId, sessionId, requesterId, tieInstant);
            tied.Add(requestId);
        }

        List<Guid> expectedTieOrder = await QueryIdsOrderedAsync(
            context,
            "SELECT id FROM join_requests WHERE created_at = @tie ORDER BY id",
            tieInstant);

        // A cursor pointing just past the last of the four distinct-`created_at` requests isolates
        // the three tied requests that follow it.
        SessionListCursor lastOfFour = new(_baseInstant + Duration.FromMinutes(3), ordered[3]);
        SessionPage<JoinRequestSummary> tiePage =
            await repository.ListJoinRequestsAsync(SessionId.From(sessionId), lastOfFour, 10, default);

        Assert.Equal(3, tiePage.Items.Count);
        Assert.Equal(expectedTieOrder, [.. tiePage.Items.Select(item => item.Id.Value)]);
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

    private static async Task InsertSessionAsync(
        AppDbContext context, UserId organizerId, Guid id, Instant startTime, short status) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO sessions
                (id, organizer_id, title, start_time, duration_minutes, platform, notes,
                 min_attendance, max_capacity, visibility, status, created_at)
            VALUES
                ({id}, {organizerId.Value}, 'Read repository test', {startTime}, 60, 3, '',
                 1, NULL, 1, {status}, now())
            """);

    private static async Task InsertParticipationAsync(
        AppDbContext context,
        Guid id,
        Guid sessionId,
        UserId userId,
        short status = 1,
        bool isWaitlisted = false) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO session_participations
                (id, session_id, user_id, status, is_waitlisted, origin, created_at)
            VALUES ({id}, {sessionId}, {userId.Value}, {status}, {isWaitlisted}, 0, now())
            """);

    private static async Task InsertJoinRequestAsync(
        AppDbContext context, Guid id, Guid sessionId, UserId requesterId, Instant createdAt) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO join_requests (id, session_id, requester_user_id, status, created_at)
            VALUES ({id}, {sessionId}, {requesterId.Value}, 0, {createdAt})
            """);

    private static async Task<List<Guid>> QueryIdsOrderedAsync(AppDbContext context, string sql, Instant tie)
    {
        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "tie";
        parameter.Value = tie.ToDateTimeOffset();
        command.Parameters.Add(parameter);

        List<Guid> ids = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
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
