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
                visibility = "public",
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

    /// <summary>
    /// TRACEABILITY.md round-2 (F3) structural fix: <c>ActionableJoinRequest</c> now carries a real
    /// requester user id, so its <see cref="PartyUp.Api.Contracts.Availability.MaskedParticipant"/>
    /// ordinal is keyed by person, exactly like every other row in the response — never by the
    /// display name every seeded account here shares ("Test Player", <c>PartyUpApiFactory.SeedUserAsync</c>).
    /// <para>
    /// P organizes a session that invites V (so P appears as <c>session.organizer</c> in V's
    /// invitations list) and separately opens a join request on V's own session (so P also appears
    /// as <c>requester</c> in V's joinRequests list) — the same person, two rows, one ordinal. Q1 and
    /// Q2 are two distinct people who both open join requests on V's sessions — sharing the same
    /// default display name proves the old name-keyed assigner would have collapsed them into one
    /// ordinal; the id-keyed assigner must not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ActionableOrdinalsAreKeyedByPersonNotDisplayName_D103()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"inbox-ord-viewer-{Guid.NewGuid():N}@example.test");
        UserId organizerRequesterId = await _factory.SeedUserAsync(email: $"inbox-ord-both-{Guid.NewGuid():N}@example.test");
        UserId requesterOneId = await _factory.SeedUserAsync(email: $"inbox-ord-r1-{Guid.NewGuid():N}@example.test");
        UserId requesterTwoId = await _factory.SeedUserAsync(email: $"inbox-ord-r2-{Guid.NewGuid():N}@example.test");
        // D-099: a non-private session needs its own directly invited friend at creation, distinct
        // from the requesters above so their eligibility to open a join-request is untouched.
        UserId fillerId = await _factory.SeedUserAsync(email: $"inbox-ord-filler-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, viewerId, organizerRequesterId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, viewerId, requesterOneId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, viewerId, requesterTwoId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, viewerId, fillerId);

        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);
        using SessionHttpSession organizerRequesterSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, organizerRequesterId);
        using SessionHttpSession requesterOneSession = await SessionEndpointTestSupport.SignInAsync(_factory, requesterOneId);
        using SessionHttpSession requesterTwoSession = await SessionEndpointTestSupport.SignInAsync(_factory, requesterTwoId);

        Instant start = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);

        // P organizes session B, inviting V — P renders as session.organizer in V's invitations.
        HttpResponseMessage sessionBCreated = await organizerRequesterSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Ordinal fixture B",
                startTime = SessionEndpointTestSupport.Iso(start),
                visibility = "public",
                minAttendance = 1,
                inviteeUserIds = new[] { viewerId.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, sessionBCreated.StatusCode);

        // V organizes two sessions of their own, so V can receive join requests.
        Guid sessionA1Id = await CreateViewerSessionAsync(viewerSession, "Ordinal fixture A1", start, fillerId);
        Guid sessionA2Id = await CreateViewerSessionAsync(
            viewerSession, "Ordinal fixture A2", start + Duration.FromHours(1), fillerId);

        // P also opens a join request on V's session — the same person as the invitations-list
        // organizer above. The create-overload response carries the raw `id` (never masked — it is
        // the requester naming themselves, JoinRequestViewResponse.From(JoinRequest)), which is the
        // only way to correlate a specific row back out of the masked `/me/actionable` list below,
        // since two requests land on the same session A1.
        using JsonDocument pRequestBody = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerRequesterSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/sessions/{sessionA1Id:D}/join-requests"));
        Guid pRequestId = pRequestBody.RootElement.GetProperty("id").GetGuid();

        // Two distinct people (same default display name) request two different sessions.
        using JsonDocument requesterOneBody = await SessionEndpointTestSupport.SuccessJsonAsync(
            await requesterOneSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/sessions/{sessionA1Id:D}/join-requests"));
        Guid requesterOneRequestId = requesterOneBody.RootElement.GetProperty("id").GetGuid();
        using JsonDocument requesterTwoBody = await SessionEndpointTestSupport.SuccessJsonAsync(
            await requesterTwoSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/sessions/{sessionA2Id:D}/join-requests"));
        Guid requesterTwoRequestId = requesterTwoBody.RootElement.GetProperty("id").GetGuid();

        using JsonDocument inbox = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync("/api/v1/me/actionable"));

        JsonElement invitationForB = inbox.RootElement.GetProperty("invitations").EnumerateArray()
            .Single(item => item.GetProperty("session").GetProperty("title").GetString() == "Ordinal fixture B");
        int organizerOrdinal = invitationForB.GetProperty("session").GetProperty("organizer").GetProperty("ordinal").GetInt32();

        JsonElement[] joinRequests = [.. inbox.RootElement.GetProperty("joinRequests").EnumerateArray()];
        int requesterPOrdinal = joinRequests
            .Single(item => item.GetProperty("joinRequestId").GetGuid() == pRequestId)
            .GetProperty("requester").GetProperty("ordinal").GetInt32();

        // Same person (P), organizer in one row and requester in another: one ordinal.
        Assert.Equal(organizerOrdinal, requesterPOrdinal);

        int requesterOneOrdinal = joinRequests
            .Single(item => item.GetProperty("joinRequestId").GetGuid() == requesterOneRequestId)
            .GetProperty("requester").GetProperty("ordinal").GetInt32();
        int requesterTwoOrdinal = joinRequests
            .Single(item => item.GetProperty("joinRequestId").GetGuid() == requesterTwoRequestId)
            .GetProperty("requester").GetProperty("ordinal").GetInt32();

        // Two distinct people sharing a display name: two ordinals, never collapsed into one.
        Assert.NotEqual(requesterOneOrdinal, requesterTwoOrdinal);
    }

    private static async Task<Guid> CreateViewerSessionAsync(
        SessionHttpSession viewerSession, string title, Instant start, UserId fillerId)
    {
        HttpResponseMessage created = await viewerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title,
                startTime = SessionEndpointTestSupport.Iso(start),
                visibility = "public",
                minAttendance = 1,
                inviteeUserIds = new[] { fillerId.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await SessionEndpointTestSupport.SuccessJsonAsync(created))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();
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
                visibility = "private",
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
