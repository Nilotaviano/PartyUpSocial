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
            visibility: ContentVisibility.Public);
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
        // D-188/D-076: the avatar is a third identity channel and must be ABSENT, not null.
        Assert.False(maskedEntry.TryGetProperty("avatar", out _));

        JsonElement tombstonedEntry = roster.EnumerateArray()
            .Single(entry => entry.TryGetProperty("userId", out JsonElement id) && id.GetGuid() == tombstonedId.Value);
        Assert.True(tombstonedEntry.TryGetProperty("userId", out JsonElement tombstonedUserIdElement));
        Assert.Equal(tombstonedId.Value, tombstonedUserIdElement.GetGuid());
        Assert.False(tombstonedEntry.TryGetProperty("displayName", out _));
        // D-092: MarkDeleted nulls User.Avatar, so a tombstoned row has no avatar either.
        Assert.False(tombstonedEntry.TryGetProperty("avatar", out _));

        // Every other entry (organizer, viewer's own row) keeps its userId — masking is scoped, not global.
        Assert.Equal(4, roster.GetArrayLength());
        int withUserId = roster.EnumerateArray().Count(entry => entry.TryGetProperty("userId", out _));
        Assert.Equal(3, withUserId);
    }

    /// <summary>
    /// D-188: every unmasked roster row carries the participant's avatar as an object, so the roster
    /// can render a face without a second round trip. Seeded users carry Avatar.FromEmoji("🙂")
    /// (PartyUpApiFactory.SeedUserAsync:99).
    /// </summary>
    [Fact]
    public async Task AnUnmaskedRosterEntryCarriesTheParticipantAvatar_D188()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"av-org-{Guid.NewGuid():N}@example.test");
        UserId participantId = await _factory.SeedUserAsync(email: $"av-part-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, participantId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Avatar fan-out fixture",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                visibility = "public",
                minAttendance = 1,
                inviteeUserIds = new[] { participantId.Value },
            }));
        Guid sessionId = (await SessionEndpointTestSupport.SuccessJsonAsync(created))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));

        JsonElement roster = detail.RootElement.GetProperty("roster");
        JsonElement organizerEntry = roster.EnumerateArray()
            .Single(entry => entry.TryGetProperty("userId", out JsonElement id) && id.GetGuid() == organizerId.Value);
        JsonElement avatar = organizerEntry.GetProperty("avatar");
        Assert.Equal("emoji", avatar.GetProperty("kind").GetString());
        Assert.Equal("🙂", avatar.GetProperty("emoji").GetString());

        JsonElement inviteeEntry = roster.EnumerateArray()
            .Single(entry => entry.TryGetProperty("userId", out JsonElement id) && id.GetGuid() == participantId.Value);
        Assert.Equal("emoji", inviteeEntry.GetProperty("avatar").GetProperty("kind").GetString());
    }

    /// <summary>
    /// AVATARS_DESIGN.md §2.4/§5 (D-188/D-076): a <c>MaskedParticipant</c> carries an avatar only where
    /// its composer resolved one from the viewer's own friend set — the calendar's friend layer. The
    /// session-list organizer is composed through the caller-resolved overload, which offers no avatar:
    /// an unattached organizer is a stranger to the viewer, and a face there would defeat the ordinal
    /// that exists in its place. This pins the omission so a later wave cannot quietly add one.
    /// </summary>
    [Fact]
    public async Task TheSessionListOrganizerMaskedParticipantCarriesNoAvatar_D188_D076()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"av-mp-org-{Guid.NewGuid():N}@example.test");
        UserId participantId = await _factory.SeedUserAsync(email: $"av-mp-part-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, participantId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "MaskedParticipant fixture",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                visibility = "public",
                minAttendance = 1,
                inviteeUserIds = new[] { participantId.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using JsonDocument list = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync("/api/v1/sessions?scope=upcoming"));
        JsonElement organizer = list.RootElement.GetProperty("items").EnumerateArray()
            .Single().GetProperty("organizer");
        Assert.True(organizer.TryGetProperty("ordinal", out _));
        Assert.False(organizer.TryGetProperty("avatar", out _));
    }

    /// <summary>
    /// AVATARS_DESIGN.md §2.4/§5: the D-188/D-076 absence assertion above (line 86) would stay green
    /// even if only the <c>emoji</c> field were nulled and the img arm's proxy token leaked through a
    /// forgotten field on a masked entry -- because the whole <c>avatar</c> key is absent, the token
    /// can't ride along in it either. This pins that down explicitly for a blocked counterpart whose
    /// avatar is an <c>External</c> one with a real, otherwise-working proxy token, so the coverage
    /// gap can't come back if a future field is ever added directly onto the roster entry.
    /// </summary>
    [Fact]
    public async Task AMaskedEntryWithAnExternalAvatarLeaksNoProxyToken_D188_D076()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"wire-tok-org-{Guid.NewGuid():N}@example.test");
        UserId viewerId = await _factory.SeedUserAsync(email: $"wire-tok-viewer-{Guid.NewGuid():N}@example.test");
        UserId blockedId = await _factory.SeedUserAsync(email: $"wire-tok-blocked-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
        AvatarSource source = blockedUser.CaptureAvatarSource(
            AvatarSourceKind.SteamPlatform,
            [1, 2, 3],
            "image/webp",
            _factory.Clock);
        string token = source.Token;
        blockedUser.ChangeAvatar(Avatar.FromSource(source.Id));
        await context.SaveChangesAsync();

        SessionDetails details = new(
            SessionTitle.Create("Token leak fixture"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Public);
        Session session = Session.Create(organizerId, details, [viewerId, blockedId], _factory.Clock);
        _ = session.DequeueEvents();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();

        Session reloaded = await context.Sessions.Include(s => s.Participations).SingleAsync(s => s.Id == session.Id);
        reloaded.Rsvp(ParticipantRef.ForUser(viewerId), ParticipationStatus.Going, _factory.Clock);
        reloaded.Rsvp(ParticipantRef.ForUser(blockedId), ParticipationStatus.Going, _factory.Clock);
        _ = reloaded.DequeueEvents();
        await context.SaveChangesAsync();

        context.Blocks.Add(Block.Create(
            viewerId, blockedId, blockedUser.Username!.Value, _factory.Clock));
        await context.SaveChangesAsync();

        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);
        HttpResponseMessage response = await viewerSession.GetAsync($"/api/v1/sessions/{session.Id.Value:D}");
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument detail = JsonDocument.Parse(body);

        JsonElement roster = detail.RootElement.GetProperty("roster");
        JsonElement maskedEntry = roster.EnumerateArray()
            .Single(entry => !entry.TryGetProperty("userId", out _)
                && !entry.TryGetProperty("displayName", out _));
        Assert.False(maskedEntry.TryGetProperty("avatar", out _));

        // Belt and suspenders: the real token never appears anywhere in the raw response body,
        // whatever shape a future regression might smuggle it in under.
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
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
                visibility = "public",
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
