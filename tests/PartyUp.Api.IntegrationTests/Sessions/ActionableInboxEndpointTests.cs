using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// PHASE5_DESIGN.md P5-HOME-01/§5.1/§8.1: <c>GET /me/actionable</c> — D-103's Home region 1 over HTTP,
/// with masked identities on both lists.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ActionableInboxEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task InvitationsAndOrganizerJoinRequestsAreReturnedWithMaskedIdentities_D103()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"inbox-org-{Guid.NewGuid():N}@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(email: $"inbox-invitee-{Guid.NewGuid():N}@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: $"inbox-requester-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, inviteeId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, requesterId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession inviteeSession = await SessionEndpointTestSupport.SignInAsync(_factory, inviteeId);
        using SessionHttpSession requesterSession = await SessionEndpointTestSupport.SignInAsync(_factory, requesterId);

        Instant start = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Actionable inbox fixture",
                startTime = SessionEndpointTestSupport.Iso(start),
                isPrivate = false,
                minAttendance = 1,
                inviteeUserIds = new[] { inviteeId.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid sessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(created))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        // Region 1's invitation half: the invitee's own inbox shows the Pending invitation, masked
        // organizer identity, viewerRelation "invitee".
        using JsonDocument inviteeInbox = await SessionEndpointTestSupport.SuccessJsonAsync(
            await inviteeSession.GetAsync("/api/v1/me/actionable"));
        JsonElement invitation = inviteeInbox.RootElement.GetProperty("invitations").EnumerateArray()
            .Single(item => item.GetProperty("session").GetProperty("id").GetGuid() == sessionId);
        JsonElement invitationOrganizer = invitation.GetProperty("session").GetProperty("organizer");
        Assert.True(invitationOrganizer.TryGetProperty("ordinal", out _));
        Assert.True(invitationOrganizer.TryGetProperty("displayName", out _));
        Assert.Equal("invitee", invitation.GetProperty("session").GetProperty("viewerRelation").GetString());
        Assert.True(inviteeInbox.RootElement.GetProperty("joinRequests").GetArrayLength() == 0
            || inviteeInbox.RootElement.GetProperty("joinRequests").EnumerateArray()
                .All(item => item.GetProperty("session").GetProperty("id").GetGuid() != sessionId));

        // Region 1's join-request half: the requester opens a request; the organizer's inbox shows it
        // with a masked requester identity and viewerRelation "organizer".
        HttpResponseMessage requested = await requesterSession.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/sessions/{sessionId:D}/join-requests");
        Assert.Equal(HttpStatusCode.Created, requested.StatusCode);

        using JsonDocument organizerInbox = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync("/api/v1/me/actionable"));
        JsonElement joinRequest = organizerInbox.RootElement.GetProperty("joinRequests").EnumerateArray()
            .Single(item => item.GetProperty("session").GetProperty("id").GetGuid() == sessionId);
        JsonElement requester = joinRequest.GetProperty("requester");
        Assert.True(requester.TryGetProperty("ordinal", out _));
        Assert.True(requester.TryGetProperty("displayName", out _));
        Assert.True(joinRequest.TryGetProperty("joinRequestId", out _));
        Assert.Equal("organizer", joinRequest.GetProperty("session").GetProperty("viewerRelation").GetString());

        Assert.True(organizerInbox.RootElement.GetProperty("hasMoreInvitations").ValueKind is JsonValueKind.False);
        Assert.True(organizerInbox.RootElement.GetProperty("hasMoreJoinRequests").ValueKind is JsonValueKind.False);
    }

    [Fact]
    public async Task HiddenAndMissingAreEquivalent_D007_D019()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"inbox-hidden-org-{Guid.NewGuid():N}@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(email: $"inbox-hidden-invitee-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, inviteeId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession inviteeSession = await SessionEndpointTestSupport.SignInAsync(_factory, inviteeId);

        Instant start = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Resolved invitation",
                startTime = SessionEndpointTestSupport.Iso(start),
                isPrivate = true,
                minAttendance = 1,
                inviteeUserIds = new[] { inviteeId.Value },
            }));
        Guid sessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(created))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        using JsonDocument beforeResponse = await SessionEndpointTestSupport.SuccessJsonAsync(
            await inviteeSession.GetAsync("/api/v1/me/actionable"));
        Assert.Contains(
            beforeResponse.RootElement.GetProperty("invitations").EnumerateArray(),
            item => item.GetProperty("session").GetProperty("id").GetGuid() == sessionId);

        // Once the invitee responds, the row is no longer a Pending invitation: the inbox becomes
        // indistinguishable from a world where the invitation never existed at all.
        HttpResponseMessage responded = await inviteeSession.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/sessions/{sessionId:D}/my-participation",
            JsonContent.Create(new { response = "Going" }));
        Assert.Equal(HttpStatusCode.OK, responded.StatusCode);

        using JsonDocument afterResponse = await SessionEndpointTestSupport.SuccessJsonAsync(
            await inviteeSession.GetAsync("/api/v1/me/actionable"));
        Assert.DoesNotContain(
            afterResponse.RootElement.GetProperty("invitations").EnumerateArray(),
            item => item.GetProperty("session").GetProperty("id").GetGuid() == sessionId);

        // A stranger who was never invited sees the same absence.
        UserId strangerId = await _factory.SeedUserAsync(email: $"inbox-hidden-stranger-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);
        using JsonDocument strangerInbox = await SessionEndpointTestSupport.SuccessJsonAsync(
            await strangerSession.GetAsync("/api/v1/me/actionable"));
        Assert.DoesNotContain(
            strangerInbox.RootElement.GetProperty("invitations").EnumerateArray(),
            item => item.GetProperty("session").GetProperty("id").GetGuid() == sessionId);
    }
}
