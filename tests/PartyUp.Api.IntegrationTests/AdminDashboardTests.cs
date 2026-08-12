using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.Endpoints;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

/// <summary>
/// D-178/D-151 (PHASE8_DESIGN.md §3 S1): the usage dashboard's five measures over the 7- and 30-day
/// windows, each seeded so the contested definitions are pinned — a canceled session stays in the
/// confirmation denominator, a finalized poll contributes exactly 1 to it (its created session is
/// excluded, D-160), a poll succeeds only through its created session reaching Confirmed/Completed
/// (D-056), and the active-user window is closed at its lower edge.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminDashboardTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Dashboard_ReportsEveryLaunchMeasureOverBothWindows_D178_D151()
    {
        Instant now = _factory.Clock.GetCurrentInstant();

        UserId adminId = await _factory.SeedUserAsync(admin: true, email: "dash-admin@example.test");
        UserId organizerA = await _factory.SeedUserAsync(email: "dash-a@example.test");
        UserId organizerB = await _factory.SeedUserAsync(email: "dash-b@example.test");
        UserId organizerC = await _factory.SeedUserAsync(email: "dash-c@example.test");
        UserId organizerD = await _factory.SeedUserAsync(email: "dash-d@example.test");
        UserId edgeIncluded = await _factory.SeedUserAsync(email: "dash-edge-in@example.test");
        UserId edgeExcluded = await _factory.SeedUserAsync(email: "dash-edge-out@example.test");

        SessionId confirmedFixed;
        SessionId canceledFixed;
        SessionId staleFixed;
        SessionId pollCreatedSession;
        PollId finalizedPoll;
        PollId openPoll;

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Session confirmed = Session.Create(
                organizerA,
                Details(now + Duration.FromHours(24), "Confirmed fixed"),
                [],
                _factory.Clock);
            confirmed.Confirm(_factory.Clock);
            confirmedFixed = confirmed.Id;

            Session canceled = Session.Create(
                organizerA,
                Details(now + Duration.FromHours(30), "Canceled fixed"),
                [],
                _factory.Clock);
            canceled.Cancel(_factory.Clock);
            canceledFixed = canceled.Id;

            Session stale = Session.Create(
                organizerD,
                Details(now + Duration.FromHours(48), "Out of both windows"),
                [],
                _factory.Clock);
            staleFixed = stale.Id;

            Poll finalized = Poll.Create(
                organizerB,
                PollDetails("Finalized poll"),
                PollDeadlineChoice.Hours24,
                [now + Duration.FromHours(72), now + Duration.FromHours(96)],
                [],
                _factory.Clock);
            finalizedPoll = finalized.Id;

            Poll open = Poll.Create(
                organizerC,
                PollDetails("Still polling"),
                PollDeadlineChoice.Hours24,
                [now + Duration.FromHours(72), now + Duration.FromHours(96)],
                [],
                _factory.Clock);
            openPoll = open.Id;

            foreach (Session session in new[] { confirmed, canceled, stale })
            {
                _ = session.DequeueEvents();
                context.Sessions.Add(session);
            }

            foreach (Poll poll in new[] { finalized, open })
            {
                _ = poll.DequeueEvents();
                context.Polls.Add(poll);
            }

            // The poll's options must exist before selected_option_id can reference one, so
            // finalization is a second SaveChanges exactly as FinalizePollUseCase performs it.
            await context.SaveChangesAsync();

            finalized.Vote(
                ParticipantRef.ForUser(organizerB),
                finalized.Options[0].Id,
                VoteChoice.Available,
                _factory.Clock);
            PollFinalizationResult finalization = finalized.Finalize(
                finalized.Options[0].Id,
                _factory.Clock);
            Assert.Equal(SessionStatus.Confirmed, finalization.Session.Status);
            pollCreatedSession = finalization.Session.Id;
            _ = finalized.DequeueEvents();
            _ = finalization.Session.DequeueEvents();
            context.Sessions.Add(finalization.Session);
            await context.SaveChangesAsync();

            // Registration dates: three accounts inside 7 days, four inside 30, the two edge
            // accounts far outside both so they move only the active-user measure.
            await SetUserCreatedAtAsync(context, adminId, now - Duration.FromDays(1));
            await SetUserCreatedAtAsync(context, organizerA, now - Duration.FromDays(3));
            await SetUserCreatedAtAsync(context, organizerB, now - Duration.FromDays(5));
            await SetUserCreatedAtAsync(context, organizerC, now - Duration.FromDays(20));
            await SetUserCreatedAtAsync(context, organizerD, now - Duration.FromDays(40));
            await SetUserCreatedAtAsync(context, edgeIncluded, now - Duration.FromDays(100));
            await SetUserCreatedAtAsync(context, edgeExcluded, now - Duration.FromDays(100));

            await SetLastSeenAtAsync(context, organizerA, now - Duration.FromDays(1));
            await SetLastSeenAtAsync(context, organizerB, now - Duration.FromDays(10));
            await SetLastSeenAtAsync(context, organizerD, now - Duration.FromDays(40));
            // Window edges: exactly at the boundary is inside, one second earlier is outside.
            await SetLastSeenAtAsync(context, edgeIncluded, now - Duration.FromDays(7));
            await SetLastSeenAtAsync(
                context,
                edgeExcluded,
                now - Duration.FromDays(7) - Duration.FromSeconds(1));

            await SetSessionCreatedAtAsync(context, confirmedFixed, now - Duration.FromDays(3));
            await SetSessionCreatedAtAsync(
                context,
                canceledFixed,
                now - Duration.FromDays(3) + Duration.FromHours(1));
            await SetSessionCreatedAtAsync(context, staleFixed, now - Duration.FromDays(40));
            await SetSessionCreatedAtAsync(context, pollCreatedSession, now - Duration.FromDays(4));
            await SetPollCreatedAtAsync(context, finalizedPoll, now - Duration.FromDays(5));
            await SetPollCreatedAtAsync(context, openPoll, now - Duration.FromDays(20));
        }

        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, adminId);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/dashboard");
        request.Headers.Add(HeaderNames.Cookie, cookie);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AdminDashboardResponse dashboard =
            (await response.Content.ReadFromJsonAsync<AdminDashboardResponse>())!;

        AdminUsageWindowResponse week = dashboard.Last7Days;
        Assert.Equal(7, week.Days);
        Assert.Equal(3, week.Registrations);
        // The admin's own authenticated request touched last_seen_at, so the admin is active too.
        Assert.Equal(3, week.ActiveUsers);
        Assert.Equal(3, week.SessionsCreated);
        Assert.Equal(1, week.PollsCreated);
        // Two fixed sessions (the canceled one included) plus the one poll — never the session that
        // poll created.
        Assert.Equal(3, week.ConfirmationDenominator);
        Assert.Equal(2, week.ConfirmationSuccesses);
        Assert.Equal(2d / 3d, week.ConfirmationRate!.Value, 10);

        AdminUsageWindowResponse month = dashboard.Last30Days;
        Assert.Equal(30, month.Days);
        Assert.Equal(4, month.Registrations);
        Assert.Equal(5, month.ActiveUsers);
        Assert.Equal(3, month.SessionsCreated);
        Assert.Equal(2, month.PollsCreated);
        Assert.Equal(4, month.ConfirmationDenominator);
        Assert.Equal(2, month.ConfirmationSuccesses);
        Assert.Equal(0.5d, month.ConfirmationRate!.Value, 10);

        Assert.Equal(30, dashboard.RepeatScheduling.Days);
        Assert.Equal(4, dashboard.RepeatScheduling.Organizers);
        Assert.Equal(1, dashboard.RepeatScheduling.RepeatOrganizers);
        Assert.Equal(0.25d, dashboard.RepeatScheduling.Rate!.Value, 10);
    }

    [Fact]
    public async Task Dashboard_CountsAPollSuccessOnlyThroughItsCreatedSession_D178_D160()
    {
        Instant now = _factory.Clock.GetCurrentInstant();
        UserId adminId = await _factory.SeedUserAsync(admin: true, email: "poll-admin@example.test");
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-organizer@example.test");

        SessionId createdSessionId;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Poll poll = Poll.Create(
                organizerId,
                PollDetails("Finalized then canceled"),
                PollDeadlineChoice.Hours24,
                [now + Duration.FromHours(72), now + Duration.FromHours(96)],
                [],
                _factory.Clock);
            _ = poll.DequeueEvents();
            context.Polls.Add(poll);
            await context.SaveChangesAsync();

            poll.Vote(
                ParticipantRef.ForUser(organizerId),
                poll.Options[0].Id,
                VoteChoice.Available,
                _factory.Clock);
            PollFinalizationResult finalization = poll.Finalize(poll.Options[0].Id, _factory.Clock);
            createdSessionId = finalization.Session.Id;
            _ = poll.DequeueEvents();
            _ = finalization.Session.DequeueEvents();
            context.Sessions.Add(finalization.Session);
            await context.SaveChangesAsync();
        }

        AdminUsageWindowResponse confirmed = (await ReadDashboardAsync(adminId)).Last7Days;
        Assert.Equal(1, confirmed.ConfirmationDenominator);
        Assert.Equal(1, confirmed.ConfirmationSuccesses);

        // Cancel the created session: the poll is still in the denominator and now fails.
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Session session = await context.Sessions.SingleAsync(s => s.Id == createdSessionId);
            session.Cancel(_factory.Clock);
            _ = session.DequeueEvents();
            await context.SaveChangesAsync();
        }

        AdminUsageWindowResponse canceled = (await ReadDashboardAsync(adminId)).Last7Days;
        Assert.Equal(1, canceled.ConfirmationDenominator);
        Assert.Equal(0, canceled.ConfirmationSuccesses);
        Assert.Equal(0d, canceled.ConfirmationRate!.Value, 10);
    }

    [Fact]
    public async Task Dashboard_IsNotFoundForANonAdmin_D177()
    {
        UserId userId = await _factory.SeedUserAsync();
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, userId);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/dashboard");
        request.Headers.Add(HeaderNames.Cookie, cookie);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<AdminDashboardResponse> ReadDashboardAsync(UserId adminId)
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, adminId);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/dashboard");
        request.Headers.Add(HeaderNames.Cookie, cookie);
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AdminDashboardResponse>())!;
    }

    private static SessionDetails Details(Instant startTime, string title) =>
        new(
            SessionTitle.Create(title),
            startTime,
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: true);

    private static PollDetails PollDetails(string title) =>
        new(
            SessionTitle.Create(title),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: true);

    private static Task<int> SetUserCreatedAtAsync(AppDbContext context, UserId id, Instant at) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE users SET created_at = {at} WHERE id = {id.Value}");

    private static Task<int> SetLastSeenAtAsync(AppDbContext context, UserId id, Instant at) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE users SET last_seen_at = {at} WHERE id = {id.Value}");

    private static Task<int> SetSessionCreatedAtAsync(AppDbContext context, SessionId id, Instant at) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sessions SET created_at = {at} WHERE id = {id.Value}");

    private static Task<int> SetPollCreatedAtAsync(AppDbContext context, PollId id, Instant at) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE polls SET created_at = {at} WHERE id = {id.Value}");

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }
}
