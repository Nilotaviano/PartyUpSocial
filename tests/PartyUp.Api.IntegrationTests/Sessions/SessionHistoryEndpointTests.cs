using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// D-221/D-222 on the wire: the history list's amended membership and its masked player roster, and
/// the recap read that sits above it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionHistoryEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// D-221: a canceled session the viewer organized is history and says so; the roster names the
    /// viewer and their friend and leaves a stranger as a bare ordinal (D-076).
    /// </summary>
    [Fact]
    public async Task History_CarriesCanceledItemsAndAMaskedPlayerRoster_D221()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser friend = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser stranger = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, friend.Id);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);

        Session played = await SessionEndpointTestSupport.SeedSessionAsync(
            _factory, organizer.Id, leadMinutes: 20, durationMinutes: 15,
            invitees: [friend.Id, stranger.Id]);
        await RsvpGoingAsync(played.Id, friend.Id, stranger.Id);

        Session dropped = await SessionEndpointTestSupport.SeedSessionAsync(
            _factory, organizer.Id, leadMinutes: 20, durationMinutes: 15);
        await CancelAsync(dropped.Id);

        _factory.Clock.Advance(Duration.FromMinutes(40));
        await SessionEndpointTestSupport.RunDueSweepAsync(_factory);

        using JsonDocument history = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync("/api/v1/sessions?scope=history"));
        JsonElement[] items = [.. history.RootElement.GetProperty("items").EnumerateArray()];

        JsonElement canceled = items.Single(item => item.GetProperty("id").GetGuid() == dropped.Id.Value);
        Assert.Equal("canceled", canceled.GetProperty("status").GetString());

        JsonElement completed = items.Single(item => item.GetProperty("id").GetGuid() == played.Id.Value);
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        JsonElement[] players = [.. completed.GetProperty("players").EnumerateArray()];
        Assert.Equal(3, players.Length);

        // The organizer leads the roster and shares the summary's own organizer ordinal.
        Assert.Equal(
            completed.GetProperty("organizer").GetProperty("ordinal").GetInt32(),
            players[0].GetProperty("ordinal").GetInt32());
        // Detail-roster parity: both guests are people this viewer played with, so both are named —
        // GET /sessions/{id} would name them too. Only the avatar is friend-set-gated, so exactly
        // one of the two carries one.
        JsonElement[] guests = [.. players.Skip(1)];
        Assert.All(guests, guest => Assert.NotNull(guest.GetProperty("displayName").GetString()));
        Assert.Single(guests, guest => guest.TryGetProperty("avatar", out _));
        Assert.Single(guests, guest => !guest.TryGetProperty("avatar", out _));
    }

    /// <summary>
    /// D-221: an invitation the viewer never took up stays out of their history even though the
    /// session itself resolves visible to them.
    /// </summary>
    [Fact]
    public async Task History_OmitsSessionsTheViewerNeverJoined_D221()
    {
        SessionHttpUser host = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser viewer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, viewer.Id);

        Session ignored = await SessionEndpointTestSupport.SeedSessionAsync(
            _factory, host.Id, leadMinutes: 20, durationMinutes: 15, invitees: [viewer.Id]);
        Session attended = await SessionEndpointTestSupport.SeedSessionAsync(
            _factory, host.Id, leadMinutes: 20, durationMinutes: 15, invitees: [viewer.Id]);
        await RsvpGoingAsync(attended.Id, viewer.Id);

        _factory.Clock.Advance(Duration.FromMinutes(40));
        await SessionEndpointTestSupport.RunDueSweepAsync(_factory);

        Assert.Equal(
            HttpStatusCode.OK,
            (await session.GetAsync($"/api/v1/sessions/{ignored.Id.Value:D}")).StatusCode);

        using JsonDocument history = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync("/api/v1/sessions?scope=history"));
        Guid[] ids = [.. history.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())];

        Assert.Contains(attended.Id.Value, ids);
        Assert.DoesNotContain(ignored.Id.Value, ids);
    }

    /// <summary>
    /// D-222: 90 + 90 minutes is 3 hours, the canceled session contributes nothing, the game played
    /// twice wins, and the friend the viewer played both sessions with is a top mate by name.
    /// </summary>
    [Fact]
    public async Task HistoryStats_RecapCompletedSessionsOnly_D222()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser mate = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, mate.Id);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);

        foreach (int _ in Enumerable.Range(0, 2))
        {
            Session played = await SessionEndpointTestSupport.SeedSessionAsync(
                _factory, organizer.Id, leadMinutes: 20, durationMinutes: 90, invitees: [mate.Id]);
            await RsvpGoingAsync(played.Id, mate.Id);
            await SetGameAsync(played.Id, "Deep Rock Galactic");
        }

        Session dropped = await SessionEndpointTestSupport.SeedSessionAsync(
            _factory, organizer.Id, leadMinutes: 20, durationMinutes: 600);
        await CancelAsync(dropped.Id);

        _factory.Clock.Advance(Duration.FromMinutes(200));
        await SessionEndpointTestSupport.RunDueSweepAsync(_factory);

        using JsonDocument stats = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync("/api/v1/sessions/history/stats"));
        JsonElement root = stats.RootElement;

        Assert.Equal(2, root.GetProperty("completedCount").GetInt32());
        Assert.Equal(3, root.GetProperty("hoursTogether").GetInt32());
        Assert.Equal("Deep Rock Galactic", root.GetProperty("topGame").GetProperty("name").GetString());
        Assert.Equal(2, root.GetProperty("topGame").GetProperty("times").GetInt32());

        JsonElement[] topMates = [.. root.GetProperty("topMates").EnumerateArray()];
        JsonElement only = Assert.Single(topMates);
        Assert.Equal(2, only.GetProperty("times").GetInt32());
        Assert.Equal("HTTP Session Test", only.GetProperty("player").GetProperty("displayName").GetString());
    }

    /// <summary>D-222: an empty history recaps as zeroes, never as a 404 or a null envelope.</summary>
    [Fact]
    public async Task HistoryStats_OfAnUntouchedAccountAreZeroed_D222()
    {
        SessionHttpUser user = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, user.Id);

        using JsonDocument stats = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync("/api/v1/sessions/history/stats"));

        Assert.Equal(0, stats.RootElement.GetProperty("completedCount").GetInt32());
        Assert.Equal(0, stats.RootElement.GetProperty("hoursTogether").GetInt32());
        Assert.Equal(JsonValueKind.Null, stats.RootElement.GetProperty("topGame").ValueKind);
        Assert.Empty(stats.RootElement.GetProperty("topMates").EnumerateArray());
    }

    /// <summary>The recap is a personal read: an anonymous caller is turned away like its siblings.</summary>
    [Fact]
    public async Task HistoryStats_RequiresAnAuthenticatedViewer_D222()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/sessions/history/stats");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task RsvpGoingAsync(SessionId sessionId, params UserId[] userIds)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Session session = await context.Sessions
            .Include(s => s.Participations)
            .SingleAsync(s => s.Id == sessionId);

        foreach (UserId userId in userIds)
        {
            session.Rsvp(ParticipantRef.ForUser(userId), ParticipationStatus.Going, _factory.Clock);
        }

        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task SetGameAsync(SessionId sessionId, string game)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Session session = await context.Sessions
            .Include(s => s.Participations)
            .SingleAsync(s => s.Id == sessionId);
        session.ChangeGame(Platform.NotApplicable, SessionGame.FreeText(GameName.Create(game)), _factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }

    private async Task CancelAsync(SessionId sessionId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Session session = await context.Sessions
            .Include(s => s.Participations)
            .SingleAsync(s => s.Id == sessionId);
        session.Cancel(_factory.Clock);
        _ = session.DequeueEvents();
        await context.SaveChangesAsync();
    }
}
