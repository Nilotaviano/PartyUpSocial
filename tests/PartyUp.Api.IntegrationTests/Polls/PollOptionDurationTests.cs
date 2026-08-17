using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// D-211 end to end over the HTTP surface: create collects one length per candidate, suggest carries
/// the suggester's own, the persisted <c>poll_options.duration_minutes</c> column holds each one, and
/// finalization mints the session from the winning option's length. D-142's grid binds per option and
/// is enforced both at the boundary (400) and by <c>ck_poll_options_duration</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollOptionDurationTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task CreatePoll_PersistsEachOptionsOwnDuration_D211()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"d211-create-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"d211-create-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);

        using JsonDocument created = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/polls",
                JsonContent.Create(new
                {
                    title = "Per-option lengths",
                    options = new[]
                    {
                        new { startTime = SessionEndpointTestSupport.Iso(baseTime), durationMinutes = 45 },
                        new { startTime = SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(3)), durationMinutes = 300 },
                    },
                    visibility = "public",
                    minAttendance = 1,
                    inviteeUserIds = new[] { friendId.Value },
                })));

        JsonElement poll = created.RootElement.GetProperty("poll");
        Guid pollId = poll.GetProperty("id").GetGuid();
        Dictionary<string, int> wireLengths = poll.GetProperty("options").EnumerateArray()
            .ToDictionary(
                option => option.GetProperty("startTime").GetString()!,
                option => option.GetProperty("durationMinutes").GetInt32());

        Assert.Equal(45, wireLengths[SessionEndpointTestSupport.Iso(baseTime)]);
        Assert.Equal(300, wireLengths[SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(3))]);

        // The column, not just the projection: the poll carries no length of its own to fall back on.
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<int> stored = await context.Set<PollOption>()
            .AsNoTracking()
            .Where(option => option.PollId == PollId.From(pollId))
            .OrderBy(option => option.StartTime)
            .Select(option => option.Duration.TotalMinutes)
            .ToListAsync();

        Assert.Equal([45, 300], stored);
    }

    [Fact]
    public async Task CreatePoll_RejectsAnOptionLengthOffTheD142Grid_D211()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"d211-grid-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/polls",
                JsonContent.Create(new
                {
                    title = "Off-grid candidate",
                    options = new[]
                    {
                        new { startTime = SessionEndpointTestSupport.Iso(baseTime), durationMinutes = 50 },
                        new { startTime = SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(3)), durationMinutes = 60 },
                    },
                    visibility = "private",
                    minAttendance = 1,
                })),
            HttpStatusCode.BadRequest,
            "poll_validation");
    }

    [Fact]
    public async Task SuggestOption_PersistsTheSuggestersOwnDuration_D211()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"d211-suggest-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"d211-suggest-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        Guid pollId = await CreatePollAsync(organizerSession, baseTime, 60, 60, friendId);

        Instant suggested = baseTime + Duration.FromHours(9);
        using JsonDocument afterSuggest = await SessionEndpointTestSupport.SuccessJsonAsync(
            await friendSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/options",
                JsonContent.Create(new
                {
                    startTime = SessionEndpointTestSupport.Iso(suggested),
                    durationMinutes = 195,
                })));

        JsonElement option = afterSuggest.RootElement.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("startTime").GetString() == SessionEndpointTestSupport.Iso(suggested));
        Assert.Equal(195, option.GetProperty("durationMinutes").GetInt32());

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int stored = await context.Set<PollOption>()
            .AsNoTracking()
            .Where(o => o.PollId == PollId.From(pollId) && o.StartTime == suggested)
            .Select(o => o.Duration.TotalMinutes)
            .SingleAsync();
        Assert.Equal(195, stored);
    }

    [Fact]
    public async Task Finalize_GivesTheSessionTheWinningOptionsOwnDuration_D211()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"d211-final-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"d211-final-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(4);
        Guid pollId = await CreatePollAsync(organizerSession, baseTime, 45, 300, friendId);

        Instant winningStart = baseTime + Duration.FromHours(3);
        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        Guid winningOptionId = detail.RootElement.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("startTime").GetString() == SessionEndpointTestSupport.Iso(winningStart))
            .GetProperty("id").GetGuid();

        // D-079/D-088: voting closes at the 24h deadline; finalize inside the selection window.
        _factory.Clock.Advance(Duration.FromHours(25));

        using JsonDocument finalized = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/polls/{pollId:D}/finalize",
                JsonContent.Create(new { optionId = winningOptionId })));
        Guid sessionId = finalized.RootElement.GetProperty("sessionId").GetGuid();

        using JsonDocument session = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        Assert.Equal(300, session.RootElement.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(
            SessionEndpointTestSupport.Iso(winningStart),
            session.RootElement.GetProperty("startTime").GetString());

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int stored = await context.Set<Session>()
            .AsNoTracking()
            .Where(s => s.Id == SessionId.From(sessionId))
            .Select(s => s.Duration.TotalMinutes)
            .SingleAsync();
        Assert.Equal(300, stored);
    }

    private static async Task<Guid> CreatePollAsync(
        SessionHttpSession organizerSession,
        Instant baseTime,
        int firstDurationMinutes,
        int secondDurationMinutes,
        UserId inviteeId)
    {
        using JsonDocument created = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/polls",
                JsonContent.Create(new
                {
                    title = "D-211 fixture poll",
                    options = new[]
                    {
                        new { startTime = SessionEndpointTestSupport.Iso(baseTime), durationMinutes = firstDurationMinutes },
                        new
                        {
                            startTime = SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(3)),
                            durationMinutes = secondDurationMinutes,
                        },
                    },
                    visibility = "public",
                    minAttendance = 1,
                    inviteeUserIds = new[] { inviteeId.Value },
                })));
        return created.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
    }
}
