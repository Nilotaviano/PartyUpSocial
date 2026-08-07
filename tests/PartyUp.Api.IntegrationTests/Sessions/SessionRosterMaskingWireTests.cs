using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// PHASE5_DESIGN.md §5.1, P5-VIS-01 (e): the two §5.1 masking signals proven on the serialized wire —
/// a blocked-pair-masked roster entry has no <c>userId</c> property at all, while a tombstoned entry in
/// the same roster keeps it; and the resolved requester name lands on the join-request summary path
/// only, closing TRACEABILITY.md:361 at the contract layer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionRosterMaskingWireTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AMaskedEntryHasNoUserIdPropertyWhileATombstonedOneKeepsIt_D076()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"wire-org-{Guid.NewGuid():N}@example.test");
        UserId viewerId = await _factory.SeedUserAsync(email: $"wire-viewer-{Guid.NewGuid():N}@example.test");
        UserId blockedId = await _factory.SeedUserAsync(email: $"wire-blocked-{Guid.NewGuid():N}@example.test");
        UserId tombstonedId = await _factory.SeedUserAsync(email: $"wire-tomb-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        SessionDetails details = new(
            SessionTitle.Create("Wire masking fixture"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: false);
        Session session = Session.Create(organizerId, details, [viewerId, blockedId, tombstonedId], _factory.Clock);
        _ = session.DequeueEvents();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();

        Session reloaded = await context.Sessions.Include(s => s.Participations).SingleAsync(s => s.Id == session.Id);
        reloaded.Rsvp(ParticipantRef.ForUser(viewerId), ParticipationStatus.Going, _factory.Clock);
        reloaded.Rsvp(ParticipantRef.ForUser(blockedId), ParticipationStatus.Going, _factory.Clock);
        reloaded.Rsvp(ParticipantRef.ForUser(tombstonedId), ParticipationStatus.Going, _factory.Clock);
        _ = reloaded.DequeueEvents();
        await context.SaveChangesAsync();

        User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
        context.Blocks.Add(Block.Create(
            viewerId, blockedId, blockedUser.Username!.Value, _factory.Clock));
        await context.SaveChangesAsync();

        User tombstonedUser = await context.DomainUsers.SingleAsync(u => u.Id == tombstonedId);
        tombstonedUser.RequestDeletion(_factory.Clock);
        _factory.Clock.Advance(Duration.FromDays(8));
        tombstonedUser.MarkDeleted(_factory.Clock);
        await context.SaveChangesAsync();

        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);
        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync($"/api/v1/sessions/{session.Id.Value:D}"));

        JsonElement roster = detail.RootElement.GetProperty("roster");
        JsonElement maskedEntry = roster.EnumerateArray()
            .Single(entry => !entry.TryGetProperty("userId", out _)
                && !entry.TryGetProperty("displayName", out _));
        Assert.False(maskedEntry.TryGetProperty("userId", out _));

        JsonElement tombstonedEntry = roster.EnumerateArray()
            .Single(entry => entry.TryGetProperty("userId", out JsonElement id) && id.GetGuid() == tombstonedId.Value);
        Assert.True(tombstonedEntry.TryGetProperty("userId", out JsonElement tombstonedUserIdElement));
        Assert.Equal(tombstonedId.Value, tombstonedUserIdElement.GetGuid());
        Assert.False(tombstonedEntry.TryGetProperty("displayName", out _));

        // Every other entry (organizer, viewer's own row) keeps its userId — masking is scoped, not global.
        Assert.Equal(4, roster.GetArrayLength());
        int withUserId = roster.EnumerateArray().Count(entry => entry.TryGetProperty("userId", out _));
        Assert.Equal(3, withUserId);
    }

    [Fact]
    public async Task JoinRequestResponsesCarryTheResolvedRequesterNameOnTheSummaryPathOnly_D103()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"wire-jr-org-{Guid.NewGuid():N}@example.test");
        UserId fillerId = await _factory.SeedUserAsync(email: $"wire-jr-filler-{Guid.NewGuid():N}@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: $"wire-jr-requester-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, fillerId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, requesterId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession requesterSession = await SessionEndpointTestSupport.SignInAsync(_factory, requesterId);

        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Join request wire fixture",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                isPrivate = false,
                minAttendance = 1,
                inviteeUserIds = new[] { fillerId.Value },
            }));
        Guid sessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(created))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        HttpResponseMessage requestResponse = await requesterSession.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/sessions/{sessionId:D}/join-requests");
        Assert.Equal(HttpStatusCode.Created, requestResponse.StatusCode);
        using JsonDocument createBody = await SessionEndpointTestSupport.SuccessJsonAsync(requestResponse);
        // The 201 create overload's sole recipient is the requester naming themselves — no resolved
        // name to offer, so the field is omitted entirely.
        Assert.False(createBody.RootElement.TryGetProperty("requester", out _));
        Assert.True(createBody.RootElement.TryGetProperty("requesterId", out _));

        using JsonDocument listBody = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{sessionId:D}/join-requests"));
        JsonElement summaryItem = listBody.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.True(summaryItem.TryGetProperty("requester", out JsonElement requester));
        Assert.True(requester.TryGetProperty("ordinal", out _));
        Assert.True(requester.TryGetProperty("displayName", out _));
        Assert.True(summaryItem.TryGetProperty("requesterId", out JsonElement requesterIdElement));
        Assert.Equal(requesterId.Value, requesterIdElement.GetGuid());
    }
}
