using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.Hosting;
using PartyUp.Api.IntegrationTests.Polls;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Application.Friends;
using PartyUp.Application.Polls;
using PartyUp.Application.Push;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Notifications;

/// <summary>
/// PHASE7_DESIGN.md §4 (ticket-P7-S4): the eleven-operation HTTP surface -- push subscriptions, the
/// notification center, session/poll mute, and the reminder-default replan. Every route is proven
/// against the real DI-wired production adapters and a real Postgres instance; <see cref="FakeConfiguredPushSender"/>
/// replaces only the transport-configured seam (§8's owner-provisioned VAPID keys), on the shipped
/// <c>PushDispatchJobTests</c>/<c>RateLimitTests.SteamRateLimitFactory</c> precedent of deriving a
/// second factory via <c>WithWebHostBuilder</c> against the same Postgres instance.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NotificationPushHttpTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    // ---- push subscriptions --------------------------------------------------------------------

    [Fact]
    public async Task GetPushConfig_VapidNotConfigured_ReportsDisabled()
    {
        SessionHttpUser user = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, user.Id);

        using JsonDocument config = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync("/api/v1/push/config"));
        Assert.False(config.RootElement.GetProperty("pushEnabled").GetBoolean());
    }

    [Fact]
    public async Task CreatePushSubscription_VapidNotConfigured_Is409()
    {
        SessionHttpUser user = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, user.Id);

        HttpResponseMessage response = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/push/subscriptions",
            JsonContent.Create(new
            {
                channel = "webpush",
                endpoint = "https://push.example.test/a",
                publicKey = "public-key",
                authSecret = "auth-secret",
            }));

        await SessionEndpointTestSupport.AssertProblemAsync(
            response, HttpStatusCode.Conflict, "push_not_configured");
    }

    [Fact]
    public async Task CreatePushSubscription_UnsupportedChannel_Is400()
    {
        await using WebApplicationFactory<Program> configured = ConfiguredPushFactory();
        UserId userId = await _factory.SeedUserAsync(email: $"p7s4-chan-{Guid.NewGuid():N}@example.test");
        using HttpClient client = Slice5HttpTest.CreateClient(configured);
        string authCookie = await SignInAsync(client, userId);

        HttpResponseMessage response = await AntiforgeryTestClient.SendAsync(
            client,
            HttpMethod.Post,
            "/api/v1/push/subscriptions",
            JsonContent.Create(new
            {
                channel = "fcm",
                endpoint = "https://push.example.test/b",
                publicKey = "public-key",
                authSecret = "auth-secret",
            }),
            authCookie);

        await SessionEndpointTestSupport.AssertProblemAsync(
            response, HttpStatusCode.BadRequest, "push_channel_unsupported");
    }

    /// <summary>The S1 verification carry: a re-POST with rotated keys keeps the id, replaces the keys.</summary>
    [Fact]
    public async Task CreatePushSubscription_RePostWithRotatedKeys_ReturnsSameIdAndStoresNewKeys_S1Carry_D112()
    {
        await using WebApplicationFactory<Program> configured = ConfiguredPushFactory();
        UserId userId = await _factory.SeedUserAsync(email: $"p7s4-rotate-{Guid.NewGuid():N}@example.test");
        using HttpClient client = Slice5HttpTest.CreateClient(configured);
        string authCookie = await SignInAsync(client, userId);
        const string endpoint = "https://push.example.test/rotate";

        HttpResponseMessage first = await AntiforgeryTestClient.SendAsync(
            client,
            HttpMethod.Post,
            "/api/v1/push/subscriptions",
            JsonContent.Create(new { channel = "webpush", endpoint, publicKey = "key-1", authSecret = "secret-1" }),
            authCookie);
        using JsonDocument firstBody = await SessionEndpointTestSupport.SuccessJsonAsync(first);
        Guid id = firstBody.RootElement.GetProperty("id").GetGuid();

        HttpResponseMessage second = await AntiforgeryTestClient.SendAsync(
            client,
            HttpMethod.Post,
            "/api/v1/push/subscriptions",
            JsonContent.Create(new { channel = "webpush", endpoint, publicKey = "key-2-rotated", authSecret = "secret-2-rotated" }),
            authCookie);
        using JsonDocument secondBody = await SessionEndpointTestSupport.SuccessJsonAsync(second);
        Assert.Equal(id, secondBody.RootElement.GetProperty("id").GetGuid());

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PushSubscription stored = await context.PushSubscriptions.AsNoTracking()
            .SingleAsync(s => s.Id == PushSubscriptionId.From(id));
        Assert.Equal("key-2-rotated", stored.PublicKey);
        Assert.Equal("secret-2-rotated", stored.AuthSecret);
    }

    [Fact]
    public async Task DeletePushSubscription_UnknownOrForeignId_Is404Identical_AndOwnDeleteIsIdempotent()
    {
        await using WebApplicationFactory<Program> configured = ConfiguredPushFactory();
        UserId ownerId = await _factory.SeedUserAsync(email: $"p7s4-del-owner-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"p7s4-del-stranger-{Guid.NewGuid():N}@example.test");
        using HttpClient client = Slice5HttpTest.CreateClient(configured);
        string ownerCookie = await SignInAsync(client, ownerId);
        string strangerCookie = await SignInAsync(client, strangerId);

        HttpResponseMessage created = await AntiforgeryTestClient.SendAsync(
            client,
            HttpMethod.Post,
            "/api/v1/push/subscriptions",
            JsonContent.Create(new
            {
                channel = "webpush",
                endpoint = "https://push.example.test/delete",
                publicKey = "key",
                authSecret = "secret",
            }),
            ownerCookie);
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid id = createdBody.RootElement.GetProperty("id").GetGuid();

        HttpResponseMessage unknown = await AntiforgeryTestClient.SendAsync(
            client, HttpMethod.Delete, $"/api/v1/push/subscriptions/{Guid.NewGuid():D}", null, ownerCookie);
        HttpResponseMessage foreign = await AntiforgeryTestClient.SendAsync(
            client, HttpMethod.Delete, $"/api/v1/push/subscriptions/{id:D}", null, strangerCookie);
        await SessionEndpointTestSupport.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "push_subscription_not_found");
        await SessionEndpointTestSupport.AssertProblemAsync(foreign, HttpStatusCode.NotFound, "push_subscription_not_found");

        HttpResponseMessage firstDelete = await AntiforgeryTestClient.SendAsync(
            client, HttpMethod.Delete, $"/api/v1/push/subscriptions/{id:D}", null, ownerCookie);
        HttpResponseMessage secondDelete = await AntiforgeryTestClient.SendAsync(
            client, HttpMethod.Delete, $"/api/v1/push/subscriptions/{id:D}", null, ownerCookie);
        Assert.Equal(HttpStatusCode.NoContent, firstDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondDelete.StatusCode);
    }

    // ---- notification center ----------------------------------------------------------------------

    [Fact]
    public async Task ListNotifications_BothScopesRejectEachOthersCursor()
    {
        SessionHttpUser user = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, user.Id);

        Instant sortKey = _factory.Clock.GetCurrentInstant();
        string allCursor = new SessionListCursor(sortKey, Guid.NewGuid(), SessionListScope.NotificationsAll).Encode();
        string unreadCursor = new SessionListCursor(sortKey, Guid.NewGuid(), SessionListScope.NotificationsUnread).Encode();

        await SessionEndpointTestSupport.AssertProblemAsync(
            await session.GetAsync($"/api/v1/notifications?scope=unread&cursor={Uri.EscapeDataString(allCursor)}"),
            HttpStatusCode.BadRequest,
            "invalid_cursor");
        await SessionEndpointTestSupport.AssertProblemAsync(
            await session.GetAsync($"/api/v1/notifications?scope=all&cursor={Uri.EscapeDataString(unreadCursor)}"),
            HttpStatusCode.BadRequest,
            "invalid_cursor");

        // A cursor issued for the same scope decodes cleanly.
        using JsonDocument accepted = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync($"/api/v1/notifications?scope=all&cursor={Uri.EscapeDataString(allCursor)}"));
        Assert.True(accepted.RootElement.TryGetProperty("items", out _));

        await SessionEndpointTestSupport.AssertProblemAsync(
            await session.GetAsync("/api/v1/notifications?scope=bogus"),
            HttpStatusCode.BadRequest,
            "invalid_scope");
    }

    [Fact]
    public async Task ListNotifications_SortsCreatedAtDescendingThenIdDescending()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser invitee = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        using SessionHttpSession inviteeSession = await SessionEndpointTestSupport.SignInAsync(_factory, invitee.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, invitee.Id);

        await CreateFutureSessionWithInviteeAsync(organizerSession, invitee.Id, leadDays: 2, title: "First invite");
        await CreateFutureSessionWithInviteeAsync(organizerSession, invitee.Id, leadDays: 3, title: "Second invite");

        using JsonDocument page = await SessionEndpointTestSupport.SuccessJsonAsync(
            await inviteeSession.GetAsync("/api/v1/notifications?scope=all"));
        List<DateTimeOffset> createdAts = [.. page.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => DateTimeOffset.Parse(
                item.GetProperty("createdAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture))];
        Assert.True(createdAts.Count >= 2);
        List<DateTimeOffset> sortedDescending = [.. createdAts.OrderByDescending(value => value)];
        Assert.Equal(sortedDescending, createdAts);
    }

    [Fact]
    public async Task GetUnreadNotificationCount_MarkRead_MarkAllRead_AreIdempotentAndFailClosed()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser invitee = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser stranger = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        using SessionHttpSession inviteeSession = await SessionEndpointTestSupport.SignInAsync(_factory, invitee.Id);
        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, stranger.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, invitee.Id);

        await CreateFutureSessionWithInviteeAsync(organizerSession, invitee.Id, leadDays: 5, title: "Mark-read probe");

        using JsonDocument before = await SessionEndpointTestSupport.SuccessJsonAsync(
            await inviteeSession.GetAsync("/api/v1/notifications/unread-count"));
        int unreadBefore = before.RootElement.GetProperty("count").GetInt32();
        Assert.True(unreadBefore >= 1);

        using JsonDocument list = await SessionEndpointTestSupport.SuccessJsonAsync(
            await inviteeSession.GetAsync("/api/v1/notifications?scope=unread"));
        Guid notificationId = list.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();

        // fail-closed: unknown id, and a foreign (non-owning) user's id, both 404.
        await SessionEndpointTestSupport.AssertProblemAsync(
            await inviteeSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/notifications/{Guid.NewGuid():D}/read"),
            HttpStatusCode.NotFound,
            "notification_not_found");
        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/notifications/{notificationId:D}/read"),
            HttpStatusCode.NotFound,
            "notification_not_found");

        // idempotent: mark-read twice.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await inviteeSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/notifications/{notificationId:D}/read")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await inviteeSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/notifications/{notificationId:D}/read")).StatusCode);

        // idempotent: mark-all-read, then again with nothing left unread.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await inviteeSession.SendUnsafeAsync(HttpMethod.Post, "/api/v1/notifications/read-all")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await inviteeSession.SendUnsafeAsync(HttpMethod.Post, "/api/v1/notifications/read-all")).StatusCode);

        using JsonDocument after = await SessionEndpointTestSupport.SuccessJsonAsync(
            await inviteeSession.GetAsync("/api/v1/notifications/unread-count"));
        Assert.Equal(0, after.RootElement.GetProperty("count").GetInt32());
    }

    /// <summary>The §4 wire rule: a blocked-pair-masked actor omits both fields; blocking does not delete the row.</summary>
    [Fact]
    public async Task NotificationWireShape_BlockedPairActorOmitsBothActorFields_D076_D086()
    {
        SessionHttpUser recipient = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser actor = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession recipientSession = await SessionEndpointTestSupport.SignInAsync(_factory, recipient.Id);
        using SessionHttpSession actorSession = await SessionEndpointTestSupport.SignInAsync(_factory, actor.Id);

        HttpResponseMessage sent = await actorSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = recipient.Username }));
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            BlockUserUseCase blocks = scope.ServiceProvider.GetRequiredService<BlockUserUseCase>();
            Assert.True((await blocks.ExecuteAsync(recipient.Id, actor.Username, default)).Succeeded);
        }

        using JsonDocument page = await SessionEndpointTestSupport.SuccessJsonAsync(
            await recipientSession.GetAsync("/api/v1/notifications?scope=all"));
        JsonElement row = page.RootElement.GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "friendRequestReceived");
        Assert.False(row.TryGetProperty("actorUserId", out _));
        Assert.False(row.TryGetProperty("actorDisplayName", out _));
        // D-188: MaskedIdentity.Anonymous carries a null avatar, so the third channel is absent too.
        Assert.False(row.TryGetProperty("actorAvatar", out _));
    }

    /// <summary>
    /// D-188: an unmasked actor's avatar reaches the notification center from row.Actor.Avatar — the
    /// MaskedIdentity the projection already built — never from a second lookup keyed on ActorUserId,
    /// which would bypass the masking rule entirely.
    /// </summary>
    [Fact]
    public async Task NotificationWireShape_NamedActorCarriesTheirAvatar_D188()
    {
        SessionHttpUser recipient = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser actor = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession recipientSession = await SessionEndpointTestSupport.SignInAsync(_factory, recipient.Id);
        using SessionHttpSession actorSession = await SessionEndpointTestSupport.SignInAsync(_factory, actor.Id);

        HttpResponseMessage sent = await actorSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = recipient.Username }));
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());

        using JsonDocument page = await SessionEndpointTestSupport.SuccessJsonAsync(
            await recipientSession.GetAsync("/api/v1/notifications?scope=all"));
        JsonElement row = page.RootElement.GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "friendRequestReceived");

        Assert.True(row.TryGetProperty("actorUserId", out _));
        JsonElement avatar = row.GetProperty("actorAvatar");
        Assert.Equal("emoji", avatar.GetProperty("kind").GetString());
        Assert.Equal("🙂", avatar.GetProperty("emoji").GetString());
    }

    // ---- session/poll mute -------------------------------------------------------------------------

    [Fact]
    public async Task MuteSession_InvisibleTarget_Is404_AndMuteUnmuteAreIdempotent_D090_D113()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser stranger = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, stranger.Id);

        Session privateSession = await SessionEndpointTestSupport.SeedSessionAsync(_factory, organizer.Id);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.SendUnsafeAsync(HttpMethod.Put, $"/api/v1/sessions/{privateSession.Id.Value:D}/mute"),
            HttpStatusCode.NotFound,
            "session_not_found");
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(HttpMethod.Put, $"/api/v1/sessions/{Guid.NewGuid():D}/mute"),
            HttpStatusCode.NotFound,
            "session_not_found");

        // idempotent both directions.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Put, $"/api/v1/sessions/{privateSession.Id.Value:D}/mute")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Put, $"/api/v1/sessions/{privateSession.Id.Value:D}/mute")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/sessions/{privateSession.Id.Value:D}/mute")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/sessions/{privateSession.Id.Value:D}/mute")).StatusCode);
    }

    /// <summary>
    /// N1 (ticket-P7-S4-notes): no shipped route exposed session mute state before this fix, so the S6
    /// session mute toggle would have been write-only (mute, reload, toggle reads off).
    /// <c>SessionDetailResponse.muted</c> now reads through <c>SessionReadUseCases.GetSessionDetailAsync</c>
    /// exactly as <c>PollDetailResponse.muted</c> does.
    /// </summary>
    [Fact]
    public async Task GetSessionDetail_MutedReadsTrueAfterPutMute_FalseAfterDeleteMute_D090_N1()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);

        Instant startTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "N1 muted-field probe",
                startTime = SessionEndpointTestSupport.Iso(startTime),
                isPrivate = true,
            }));
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid sessionId = createdBody.RootElement.GetProperty("session").GetProperty("id").GetGuid();
        Assert.False(createdBody.RootElement.GetProperty("session").GetProperty("muted").GetBoolean());

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Put, $"/api/v1/sessions/{sessionId:D}/mute")).StatusCode);

        using JsonDocument muted = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        Assert.True(muted.RootElement.GetProperty("muted").GetBoolean());

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/sessions/{sessionId:D}/mute")).StatusCode);

        using JsonDocument unmuted = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        Assert.False(unmuted.RootElement.GetProperty("muted").GetBoolean());
    }

    [Fact]
    public async Task MutePoll_InvisibleTarget_Is404_D090()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"p7s4-pmute-org-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"p7s4-pmute-str-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);

        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Invisible-to-stranger poll",
                optionStartTimes = new[]
                {
                    SessionEndpointTestSupport.Iso(baseTime),
                    SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(1)),
                },
                isPrivate = true,
                minAttendance = 1,
            }));
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = createdBody.RootElement.GetProperty("poll").GetProperty("id").GetGuid();

        // A stranger with no friendship/invite has no visibility -- fails closed identically to an
        // unknown poll id.
        await SessionEndpointTestSupport.AssertProblemAsync(
            await strangerSession.SendUnsafeAsync(HttpMethod.Put, $"/api/v1/polls/{pollId:D}/mute"),
            HttpStatusCode.NotFound,
            "poll_not_found");
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(HttpMethod.Put, $"/api/v1/polls/{Guid.NewGuid():D}/mute"),
            HttpStatusCode.NotFound,
            "poll_not_found");
    }

    /// <summary>
    /// §4: UnmutePoll on a still-live (Polling) poll removes the poll-keyed row directly, through the
    /// <c>RemoveForTargetAsync(actorId, null, pollId, ct)</c> branch -- distinct from the
    /// re-parented-session branch <c>MutePoll_TerminalPoll_Refused_UnmutePoll_OnFinalizedRemovesReparentedRow_D113</c>
    /// exercises, and the branch whose EF null-parameter translation the S4 blind verification could
    /// not verify empirically.
    /// </summary>
    [Fact]
    public async Task UnmutePoll_OnALivePoll_RemovesThePollKeyedRowDirectly_D090()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"p7s4-plive-org-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Live poll mute/unmute",
                optionStartTimes = new[]
                {
                    SessionEndpointTestSupport.Iso(baseTime),
                    SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(1)),
                },
                isPrivate = true,
                minAttendance = 1,
            }));
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = createdBody.RootElement.GetProperty("poll").GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Put, $"/api/v1/polls/{pollId:D}/mute")).StatusCode);

        using JsonDocument muted = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        Assert.True(muted.RootElement.GetProperty("muted").GetBoolean());

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/mute")).StatusCode);

        using JsonDocument unmuted = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        Assert.False(unmuted.RootElement.GetProperty("muted").GetBoolean());

        await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        bool anyMuteLeft = await verifyContext.NotificationMutes.AsNoTracking()
            .AnyAsync(mute => mute.UserId == organizerId && mute.PollId == PollId.From(pollId));
        Assert.False(anyMuteLeft);
    }

    /// <summary>
    /// §4: MutePoll on a terminal poll is refused with poll_invalid_transition; UnmutePoll on a
    /// Finalized poll is not refused and removes the re-parented row, so muted reads false through
    /// the poll (via CreatedSessionId) afterwards (_D113).
    /// </summary>
    [Fact]
    public async Task MutePoll_TerminalPoll_Refused_UnmutePoll_OnFinalizedRemovesReparentedRow_D113()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"p7s4-mute-org-{Guid.NewGuid():N}@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(email: $"p7s4-mute-inv-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedContext = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PollTestHelpers.SeedAcceptedFriendshipAsync(seedContext, organizerId, inviteeId, _factory.Clock);
        await seedContext.SaveChangesAsync();

        Instant now = _factory.Clock.GetCurrentInstant();
        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "S4 mute finalize poll",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                IsPrivate: false,
                OptionStartTimes: [now + Duration.FromDays(2), now + Duration.FromDays(3)],
                DeadlineChoice: PollDeadlineChoice.Hours6,
                InviteeUserIds: [inviteeId],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);
        PollId pollId = created.Poll!.Id;
        PollOptionId optionId = created.Poll!.Options[0].Id;

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        // Mute while still Polling.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Put, $"/api/v1/polls/{pollId.Value:D}/mute")).StatusCode);

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            Assert.True((await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, inviteeId, optionId, VoteChoice.Available, default)).Succeeded);
        }

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await using AsyncServiceScope finalizeScope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(finalizeScope)
            .FinalizeAsync(pollId, organizerId, optionId, default);
        Assert.True(result.Succeeded, result.Code);
        SessionId createdSessionId = result.Session!.Id;

        // MutePoll on a terminal poll is refused -- no new code.
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.SendUnsafeAsync(HttpMethod.Put, $"/api/v1/polls/{pollId.Value:D}/mute"),
            HttpStatusCode.Conflict,
            "poll_invalid_transition");

        // The re-parented row: muted reads true on the poll (through CreatedSessionId).
        using JsonDocument pollDetail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId.Value:D}"));
        Assert.True(pollDetail.RootElement.GetProperty("muted").GetBoolean());

        // UnmutePoll on Finalized is not refused; removes the re-parented (session-keyed) row.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{pollId.Value:D}/mute")).StatusCode);

        using JsonDocument pollDetailAfter = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId.Value:D}"));
        Assert.False(pollDetailAfter.RootElement.GetProperty("muted").GetBoolean());

        await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        bool anyMuteLeft = await verifyContext.NotificationMutes.AsNoTracking()
            .AnyAsync(mute => mute.UserId == organizerId &&
                (mute.SessionId == createdSessionId || mute.PollId == pollId));
        Assert.False(anyMuteLeft);
    }

    // ---- reminder-default replan --------------------------------------------------------------------

    /// <summary>
    /// §4's D-082 gap: a default-change replans every existing future handoff to the NEW offset,
    /// through the explicit-parameter path (not a re-read).
    /// </summary>
    [Fact]
    public async Task SetReminderDefault_ReplansFutureHandoffsAgainstTheNewOffset_D082()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);

        Instant startTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(3);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "S4 replan probe",
                startTime = SessionEndpointTestSupport.Iso(startTime),
                isPrivate = true,
            }));
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid sessionId = createdBody.RootElement.GetProperty("session").GetProperty("id").GetGuid();

        // Organizer's reminder handoff is planned at the account default (Hours1) at create time.
        await using (AsyncServiceScope beforeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext beforeContext = beforeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var beforeHandoff = await beforeContext.SessionReminderHandoffs.AsNoTracking()
                .SingleAsync(h => h.SessionId == SessionId.From(sessionId) && h.RecipientUserId == organizer.Id);
            Assert.Equal(startTime - Duration.FromHours(1), beforeHandoff.DueAt);
        }

        HttpResponseMessage changed = await organizerSession.SendUnsafeAsync(
            HttpMethod.Put,
            "/api/v1/me/reminder-default",
            JsonContent.Create(new { reminderDefault = "Hours24" }));
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());

        await using AsyncServiceScope afterScope = _factory.Services.CreateAsyncScope();
        AppDbContext afterContext = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var afterHandoff = await afterContext.SessionReminderHandoffs.AsNoTracking()
            .SingleAsync(h => h.SessionId == SessionId.From(sessionId) && h.RecipientUserId == organizer.Id);
        Assert.Equal(startTime - Duration.FromHours(24), afterHandoff.DueAt);
    }

    // ---- push-prompt eligibility -------------------------------------------------------------------

    /// <summary>
    /// §4/ruling 4: a brand-new onboarded user has no qualifying action yet and reads false; creating a
    /// session leaves the caller a <c>session_participations</c> row as organizer, which the EXISTS
    /// picks up on the very next read (_D147).
    /// </summary>
    [Fact]
    public async Task MyProfile_PushPromptEligible_FalseUntilAQualifyingAction_ThenTrue_D147()
    {
        SessionHttpUser user = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, user.Id);

        using JsonDocument before = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync("/api/v1/me"));
        Assert.False(before.RootElement.GetProperty("pushPromptEligible").GetBoolean());

        Instant startTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "D-147 qualifying session",
                startTime = SessionEndpointTestSupport.Iso(startTime),
                isPrivate = true,
            }));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        using JsonDocument after = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync("/api/v1/me"));
        Assert.True(after.RootElement.GetProperty("pushPromptEligible").GetBoolean());
    }

    // ---- op-id inventory -------------------------------------------------------------------------

    /// <summary>
    /// A real closure, not the tautology it replaces: the eleven S4 ids must all be PRESENT in the
    /// live OpenAPI op-id set AND the total op-id count must equal the pinned total — so a spurious
    /// twelfth S4-family operation (present but never named here) still turns this red, which
    /// asserting only "the eleven are distinct" over a literal never could.
    /// </summary>
    [Fact]
    public async Task OperationIds_S4Surface_AddsExactlyElevenIds()
    {
        string[] expected =
        [
            "GetPushConfig",
            "CreatePushSubscription",
            "DeletePushSubscription",
            "ListNotifications",
            "GetUnreadNotificationCount",
            "MarkNotificationRead",
            "MarkAllNotificationsRead",
            "MuteSession",
            "UnmuteSession",
            "MutePoll",
            "UnmutePoll",
        ];

        // Pinned against the live op-id total this test's own factory serves (verified: 141 after
        // D-178's GetAdminDashboard op-id on top of D-194's SearchSteamCatalog, D-193's
        // InvitePollUsers, D-189's ChangeMyEmailNotifications + UnsubscribeEmailNotifications, which
        // includes the test-only `_slice2` debug sign-in endpoints the production host never
        // registers) -- a grown-by-one total fails here even though every named id is still
        // individually present.
        const int expectedTotalOperationIdCount = 141;

        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        List<string> operationIds = [];
        foreach (JsonProperty path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (JsonProperty operation in path.Value.EnumerateObject())
            {
                if (operation.Value.TryGetProperty("operationId", out JsonElement id))
                {
                    operationIds.Add(id.GetString()!);
                }
            }
        }

        foreach (string id in expected)
        {
            Assert.Contains(id, operationIds);
        }

        Assert.Equal(expectedTotalOperationIdCount, operationIds.Count);
        Assert.Equal(operationIds.Count, operationIds.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- problem-code inventory -------------------------------------------------------------------

    /// <summary>
    /// Mirrors <c>GameSessionEndpointTests.EveryDocumentedProblemCodeMapsToItsDocumentedStatus</c> /
    /// <c>PollProblemCodeClosureTests.EveryDocumentedPollProblemCodeIsExercisedOrAcceptedAsAResidual</c>'s
    /// closure idiom, scoped to the four Phase 7 S4 codes appended after the Phase 6 poll section — a
    /// spurious fifth code appended to <see cref="PartyUpOpenApiTransformer"/>'s <c>ProblemCodes</c>
    /// array turns this red until it lands in one of the covered sets.
    /// </summary>
    [Fact]
    public void EveryDocumentedPhase7ProblemCodeIsExercisedOrAcceptedAsAResidual()
    {
        HashSet<string> exercisedInThisFile = new(StringComparer.Ordinal)
        {
            "notification_not_found", // GetUnreadNotificationCount_MarkRead_MarkAllRead_AreIdempotentAndFailClosed
            "push_subscription_not_found", // DeletePushSubscription_UnknownOrForeignId_Is404Identical_AndOwnDeleteIsIdempotent
            "push_not_configured", // CreatePushSubscription_VapidNotConfigured_Is409
            "push_channel_unsupported", // CreatePushSubscription_UnsupportedChannel_Is400
        };

        Assert.Equal(
            Phase7DocumentedProblemCodes().OrderBy(code => code, StringComparer.Ordinal),
            exercisedInThisFile.OrderBy(code => code, StringComparer.Ordinal));
    }

    /// <summary>
    /// Reflects <see cref="PartyUpOpenApiTransformer"/>'s private problem-code registry, scoped to the
    /// Phase 7 S4 section (from <c>notification_not_found</c> up to the Phase 8 section anchored by
    /// <c>scope_contended</c>) — the same reflection idiom as
    /// <c>GameSessionEndpointTests.Phase4DocumentedProblemCodes</c> and
    /// <c>PollProblemCodeClosureTests.PollDocumentedProblemCodes</c>. Phase 8 codes get their own
    /// closure test as that surface grows; <c>scope_contended</c> is exercised by
    /// <c>ContentRemovalTests</c>.
    /// </summary>
    private static string[] Phase7DocumentedProblemCodes()
    {
        FieldInfo field = typeof(PartyUpOpenApiTransformer).GetField(
            "ProblemCodes", BindingFlags.NonPublic | BindingFlags.Static)!;
        var allCodes = (string[])field.GetValue(null)!;
        int phase7Start = Array.IndexOf(allCodes, "notification_not_found");
        int phase8Start = Array.IndexOf(allCodes, "scope_contended");
        return phase8Start < 0 ? allCodes[phase7Start..] : allCodes[phase7Start..phase8Start];
    }

    // ---- shared helpers ------------------------------------------------------------------------

    private static async Task CreateFutureSessionWithInviteeAsync(
        SessionHttpSession organizerSession,
        UserId inviteeId,
        int leadDays,
        string title)
    {
        Instant startTime = SystemClock.Instance.GetCurrentInstant() + Duration.FromDays(leadDays);
        HttpResponseMessage response = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title,
                startTime = SessionEndpointTestSupport.Iso(startTime),
                isPrivate = true,
                inviteeUserIds = new[] { inviteeId.Value },
            }));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Slice5HttpTest.AuthCookie(response);
    }

    /// <summary>
    /// A second host sharing this test's Postgres instance, its only difference the
    /// <see cref="IPushSender"/> registration -- reports configured without an owner-provisioned VAPID
    /// key, the seam §4's key-rotation/channel-validation tests need.
    /// </summary>
    private WebApplicationFactory<Program> ConfiguredPushFactory() =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPushSender>();
                services.AddSingleton<IPushSender>(new FakeConfiguredPushSender());
            }));

    private sealed class FakeConfiguredPushSender : IPushSender
    {
        public bool IsConfigured => true;

        public Task<PushSendResult> SendAsync(
            PushSubscriptionTarget target,
            PushPayload payload,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PushSendResult(PushSendOutcome.Delivered, 201));
    }
}
