using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using NodaTime.Text;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md A-080/P5-HTTP-01: <c>GET /calendar</c>'s wire shape — the friend layer discloses no
/// authoring identity, and the poll layer is declared and always empty in Phase 5.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CalendarEndpointTests : IAsyncLifetime, IDisposable
{
    /// <summary>
    /// D-188: a readable friend-availability owner carries all three — the ordinal, the name, and the
    /// avatar the calendar's crew list renders. A masked owner would carry the first two only
    /// (<see cref="MaskedParticipantAvatarTests"/>).
    /// </summary>
    private static readonly string[] ExpectedOwnerProperties = ["avatar", "displayName", "ordinal"];

    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task FriendLayerCarriesNoAuthoringIdentity_A080()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"cal-viewer-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"cal-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, viewerId, friendId);

        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        Instant blockStart = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage createdBlock = await friendSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/me/availability/blocks",
            JsonContent.Create(new
            {
                startTime = Iso(blockStart),
                endTime = Iso(blockStart + Duration.FromHours(2)),
            }));
        Assert.Equal(HttpStatusCode.Created, createdBlock.StatusCode);

        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);
        Instant from = _factory.Clock.GetCurrentInstant();
        Instant to = from + Duration.FromDays(7);
        HttpResponseMessage calendar = await viewerSession.GetAsync(
            $"/api/v1/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}");
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(calendar);

        JsonElement friendAvailability = body.RootElement.GetProperty("friendAvailability");
        Assert.True(friendAvailability.GetArrayLength() >= 1);
        foreach (JsonElement entry in friendAvailability.EnumerateArray())
        {
            // A-080: {owner: MaskedParticipant, ranges} only — no block/series id, ever.
            JsonElement owner = entry.GetProperty("owner");
            Assert.True(owner.TryGetProperty("ordinal", out _));
            Assert.False(entry.TryGetProperty("blockId", out _));
            Assert.False(entry.TryGetProperty("seriesId", out _));
            Assert.False(owner.TryGetProperty("id", out _));

            string[] ownerProperties = [.. owner.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
            Assert.Equal(ExpectedOwnerProperties, ownerProperties);
        }
    }

    /// <summary>
    /// D-188 on the calendar's friend layer: the crew list renders a face, so a readable owner's avatar
    /// arrives with the layer rather than costing a per-friend round trip. Seeded users carry
    /// <c>Avatar.FromEmoji("🙂")</c> (PartyUpApiFactory.SeedUserAsync).
    /// </summary>
    [Fact]
    public async Task FriendLayerOwnersCarryTheirAvatar_D188()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"cal-av-viewer-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"cal-av-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, viewerId, friendId);

        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        Instant blockStart = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        Assert.Equal(
            HttpStatusCode.Created,
            (await friendSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/me/availability/blocks",
                JsonContent.Create(new
                {
                    startTime = Iso(blockStart),
                    endTime = Iso(blockStart + Duration.FromHours(2)),
                }))).StatusCode);

        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);
        Instant from = _factory.Clock.GetCurrentInstant();
        Instant to = from + Duration.FromDays(7);
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync(
                $"/api/v1/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}"));

        JsonElement owner = body.RootElement.GetProperty("friendAvailability").EnumerateArray()
            .Single().GetProperty("owner");
        Assert.False(owner.GetProperty("displayName").ValueKind == JsonValueKind.Null);
        JsonElement avatar = owner.GetProperty("avatar");
        Assert.Equal("emoji", avatar.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(avatar.GetProperty("emoji").GetString()));
    }

    /// <summary>
    /// round-1 fix (F3): <c>MaskedParticipant.ordinal</c> is per-person-stable within one response, not
    /// hardcoded 0 for every single-person context. <c>friendA</c> appears twice in this one <c>/calendar</c>
    /// response — once as a friend-availability owner, once as a visible session's organizer — and must
    /// carry the same ordinal both times; <c>friendB</c>, a different person, must carry a different one.
    /// </summary>
    [Fact]
    public async Task MaskedParticipantOrdinalsArePerPersonStableWithinOneResponseAndDistinctAcrossPeople_F3()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"cal-ord-viewer-{Guid.NewGuid():N}@example.test");
        UserId friendAId = await _factory.SeedUserAsync(email: $"cal-ord-a-{Guid.NewGuid():N}@example.test");
        UserId friendBId = await _factory.SeedUserAsync(email: $"cal-ord-b-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, viewerId, friendAId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, viewerId, friendBId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, friendAId, friendBId);

        using SessionHttpSession friendASession = await SessionEndpointTestSupport.SignInAsync(_factory, friendAId);
        using SessionHttpSession friendBSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendBId);

        Instant blockStart = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        Assert.Equal(
            HttpStatusCode.Created,
            (await friendASession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/me/availability/blocks",
                JsonContent.Create(new
                {
                    startTime = Iso(blockStart),
                    endTime = Iso(blockStart + Duration.FromHours(2)),
                }))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await friendBSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/me/availability/blocks",
                JsonContent.Create(new
                {
                    startTime = Iso(blockStart),
                    endTime = Iso(blockStart + Duration.FromHours(2)),
                }))).StatusCode);

        // friendA organizes a session inviting friendB (D-099); friendB organizes a second session
        // inviting friendA — the viewer sees both through the "any" access-kind calendar layer as a
        // friend of each organizer.
        Guid sessionByAId = await CreateVisibleSessionAsync(friendASession, "Ordinal fixture A", friendBId);
        Guid sessionByBId = await CreateVisibleSessionAsync(friendBSession, "Ordinal fixture B", friendAId);

        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);
        Instant from = _factory.Clock.GetCurrentInstant();
        Instant to = from + Duration.FromDays(7);
        HttpResponseMessage calendar = await viewerSession.GetAsync(
            $"/api/v1/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}");
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(calendar);

        JsonElement friendAvailability = body.RootElement.GetProperty("friendAvailability");
        JsonElement sessions = body.RootElement.GetProperty("sessions");

        // Both friendA and friendB share the shipped default display name ("Test Player" —
        // PartyUpApiFactory.SeedUserAsync), so a masked response's ordinal is the ONLY correlation
        // signal available — exactly the property this test proves. friendAvailability therefore
        // carries exactly two owner entries with two distinct ordinals (distinct-people half of F3);
        // each session's organizer ordinal must equal exactly one of those two pre-existing ordinals
        // (same-person-same-ordinal-across-response-sections half), and the two organizers' ordinals
        // must differ from each other.
        int[] friendOwnerOrdinals = [.. friendAvailability.EnumerateArray()
            .Select(entry => entry.GetProperty("owner").GetProperty("ordinal").GetInt32())];
        Assert.Equal(2, friendOwnerOrdinals.Length);
        Assert.Equal(2, friendOwnerOrdinals.Distinct().Count());

        JsonElement sessionByA = sessions.EnumerateArray()
            .Single(s => s.GetProperty("id").GetGuid() == sessionByAId);
        JsonElement sessionByB = sessions.EnumerateArray()
            .Single(s => s.GetProperty("id").GetGuid() == sessionByBId);
        int organizerAOrdinal = sessionByA.GetProperty("organizer").GetProperty("ordinal").GetInt32();
        int organizerBOrdinal = sessionByB.GetProperty("organizer").GetProperty("ordinal").GetInt32();

        Assert.Contains(organizerAOrdinal, friendOwnerOrdinals);
        Assert.Contains(organizerBOrdinal, friendOwnerOrdinals);
        Assert.NotEqual(organizerAOrdinal, organizerBOrdinal);
    }

    private async Task<Guid> CreateVisibleSessionAsync(SessionHttpSession organizerSession, string title, UserId inviteeId)
    {
        Instant start = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title,
                startTime = Iso(start),
                isPrivate = false,
                minAttendance = 1,
                inviteeUserIds = new[] { inviteeId.Value },
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await SessionEndpointTestSupport.SuccessJsonAsync(created))
            .RootElement.GetProperty("session").GetProperty("id").GetGuid();
    }

    /// <summary>
    /// ticket-P6-S4c: the poll layer fills (PHASE6_DESIGN.md §5 calendar bullet, D-111) -- an
    /// involved poll's in-range option instants populate <c>pollOptions</c>, replacing the Phase 5
    /// declared-and-empty pin. Out-of-range options and a poll the viewer is not involved in stay
    /// absent, so the layer is genuinely range- and rule-1-scoped, not a bare "poll exists" flag.
    /// </summary>
    [Fact]
    public async Task PollLayerPopulatesInvolvedPollOptionsInRange_D111()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"cal-poll-org-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        Instant inRangeStart = _factory.Clock.GetCurrentInstant() + Duration.FromDays(2);
        Instant outOfRangeStart = _factory.Clock.GetCurrentInstant() + Duration.FromDays(30);

        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Calendar poll layer fixture",
                optionStartTimes = new[] { Iso(inRangeStart), Iso(outOfRangeStart) },
                isPrivate = true,
                minAttendance = 1,
            }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        JsonElement pollElement = createdBody.RootElement.GetProperty("poll");
        Guid pollId = pollElement.GetProperty("id").GetGuid();
        Guid inRangeOptionId = pollElement.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("startTime").GetString() == Iso(inRangeStart))
            .GetProperty("id").GetGuid();

        Instant from = _factory.Clock.GetCurrentInstant();
        Instant to = from + Duration.FromDays(7);
        HttpResponseMessage calendar = await organizerSession.GetAsync(
            $"/api/v1/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}");
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(calendar);

        JsonElement pollOptions = body.RootElement.GetProperty("pollOptions");
        Assert.Equal(JsonValueKind.Array, pollOptions.ValueKind);
        JsonElement pollLayerEntry = Assert.Single(pollOptions.EnumerateArray());
        Assert.Equal(pollId, pollLayerEntry.GetProperty("pollId").GetGuid());
        Assert.Equal("Calendar poll layer fixture", pollLayerEntry.GetProperty("title").GetString());

        // Only the in-range instant is present -- the out-of-range option is filtered, not merely
        // unasserted (D-111's flip must be a genuine range scope, not "one poll = one row").
        JsonElement optionsElement = pollLayerEntry.GetProperty("options");
        JsonElement optionEntry = Assert.Single(optionsElement.EnumerateArray());
        Assert.Equal(inRangeOptionId, optionEntry.GetProperty("optionId").GetGuid());
        Assert.False(optionEntry.TryGetProperty("ownVote", out _));

        // Every declared layer is present on the one atomic response (A-080).
        Assert.True(body.RootElement.TryGetProperty("myAvailability", out _));
        Assert.True(body.RootElement.TryGetProperty("friendAvailability", out _));
        Assert.True(body.RootElement.TryGetProperty("sessions", out _));
    }

    /// <summary>A viewer with no involvement in any poll still gets the declared-empty shape (D-111's original claim, narrowed).</summary>
    [Fact]
    public async Task PollLayerStaysEmptyForAnUninvolvedViewer_D111()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"cal-polls-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        Instant from = _factory.Clock.GetCurrentInstant();
        Instant to = from + Duration.FromDays(7);
        HttpResponseMessage calendar = await session.GetAsync(
            $"/api/v1/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}");
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(calendar);

        JsonElement pollOptions = body.RootElement.GetProperty("pollOptions");
        Assert.Equal(JsonValueKind.Array, pollOptions.ValueKind);
        Assert.Empty(pollOptions.EnumerateArray());
    }

    /// <summary>
    /// D-025: the summary surface addresses recurring rules, not the week's expanded marks, so the
    /// viewer's own series definitions ride the <c>myAvailability</c> layer. The fixture's only
    /// occurrence in the window is skipped, so <c>occurrences</c> is empty — the definition must still
    /// come back, which is the whole point of the field: grouping the visible week's occurrences would
    /// lose this series entirely.
    /// </summary>
    [Fact]
    public async Task Calendar_ReturnsTheViewersSeriesDefinitions_D025()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"cal-series-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        // A single-occurrence series (effectiveUntil the day after its first date) inside the
        // A-076-padded fetch window, whose one occurrence is then skipped.
        LocalDate occurrenceDate = NextTuesday(_factory.Clock.GetCurrentInstant().InUtc().Date.PlusDays(1));
        string occurrenceDateText = LocalDatePattern.Iso.Format(occurrenceDate);
        HttpResponseMessage createdSeries = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/me/availability/series",
            JsonContent.Create(new
            {
                dayOfWeek = (int)IsoDayOfWeek.Tuesday,
                startTime = "20:00",
                durationMinutes = 180,
                effectiveFrom = occurrenceDateText,
                effectiveUntil = LocalDatePattern.Iso.Format(occurrenceDate.PlusDays(1)),
            }));
        Assert.Equal(HttpStatusCode.Created, createdSeries.StatusCode);
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(createdSeries);
        Guid seriesId = createdBody.RootElement.GetProperty("seriesId").GetGuid();

        HttpResponseMessage skipped = await session.SendUnsafeAsync(
            HttpMethod.Delete,
            $"/api/v1/me/availability/series/{seriesId:D}/occurrences/{occurrenceDateText}");
        Assert.Equal(HttpStatusCode.NoContent, skipped.StatusCode);

        Instant from = _factory.Clock.GetCurrentInstant();
        Instant to = from + Duration.FromDays(14);
        HttpResponseMessage calendar = await session.GetAsync(
            $"/api/v1/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}");
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(calendar);

        JsonElement myAvailability = body.RootElement.GetProperty("myAvailability");
        Assert.Empty(myAvailability.GetProperty("occurrences").EnumerateArray());

        JsonElement only = Assert.Single(myAvailability.GetProperty("series").EnumerateArray());
        Assert.Equal(seriesId, only.GetProperty("seriesId").GetGuid());
        Assert.Equal((int)IsoDayOfWeek.Tuesday, only.GetProperty("dayOfWeek").GetInt32());
        Assert.Equal("20:00", only.GetProperty("startTime").GetString());
        Assert.Equal(180, only.GetProperty("durationMinutes").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(only.GetProperty("zoneId").GetString()));
        Assert.Equal(occurrenceDateText, only.GetProperty("effectiveFrom").GetString());
    }

    /// <summary>An empty series list serializes as <c>[]</c>, so the client never distinguishes absent from empty.</summary>
    [Fact]
    public async Task Calendar_SerializesAnEmptySeriesListRatherThanOmittingIt_D025()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"cal-noseries-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        Instant from = _factory.Clock.GetCurrentInstant();
        Instant to = from + Duration.FromDays(7);
        HttpResponseMessage calendar = await session.GetAsync(
            $"/api/v1/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}");
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(calendar);

        JsonElement series = body.RootElement.GetProperty("myAvailability").GetProperty("series");
        Assert.Equal(JsonValueKind.Array, series.ValueKind);
        Assert.Empty(series.EnumerateArray());
    }

    /// <summary>
    /// D-025: the rule list is not window-scoped. A series whose first occurrence is a year out has
    /// no occurrence in the requested week and is also outside A-076's padded series fetch, so it can
    /// only appear if the layer reads the owner's rules independently of the window.
    /// </summary>
    [Fact]
    public async Task Calendar_ReturnsASeriesWhoseFirstOccurrenceIsBeyondTheWindow_D025()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"cal-future-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        LocalDate farFuture = NextTuesday(
            _factory.Clock.GetCurrentInstant().InUtc().Date.PlusDays(365));
        HttpResponseMessage createdSeries = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/me/availability/series",
            JsonContent.Create(new
            {
                dayOfWeek = (int)IsoDayOfWeek.Tuesday,
                startTime = "20:00",
                durationMinutes = 180,
                effectiveFrom = LocalDatePattern.Iso.Format(farFuture),
                effectiveUntil = (string?)null,
            }));
        Assert.Equal(HttpStatusCode.Created, createdSeries.StatusCode);
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(createdSeries);
        Guid seriesId = createdBody.RootElement.GetProperty("seriesId").GetGuid();

        Instant from = _factory.Clock.GetCurrentInstant();
        Instant to = from + Duration.FromDays(7);
        HttpResponseMessage calendar = await session.GetAsync(
            $"/api/v1/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}");
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(calendar);

        JsonElement myAvailability = body.RootElement.GetProperty("myAvailability");
        Assert.Empty(myAvailability.GetProperty("occurrences").EnumerateArray());

        JsonElement only = Assert.Single(myAvailability.GetProperty("series").EnumerateArray());
        Assert.Equal(seriesId, only.GetProperty("seriesId").GetGuid());
        Assert.Equal(LocalDatePattern.Iso.Format(farFuture), only.GetProperty("effectiveFrom").GetString());
    }

    /// <summary>
    /// The trailing bound stays: a series that already ended is history, not a rule the owner can
    /// still act on, so dropping the window's upper bound must not resurrect expired rules.
    /// </summary>
    [Fact]
    public async Task Calendar_OmitsASeriesThatEndedBeforeTheWindow_D025()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"cal-expired-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        LocalDate today = _factory.Clock.GetCurrentInstant().InUtc().Date;
        LocalDate startedLongAgo = NextTuesday(today.PlusDays(-400));
        HttpResponseMessage createdSeries = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/me/availability/series",
            JsonContent.Create(new
            {
                dayOfWeek = (int)IsoDayOfWeek.Tuesday,
                startTime = "20:00",
                durationMinutes = 180,
                effectiveFrom = LocalDatePattern.Iso.Format(startedLongAgo),
                effectiveUntil = LocalDatePattern.Iso.Format(startedLongAgo.PlusDays(7)),
            }));
        Assert.Equal(HttpStatusCode.Created, createdSeries.StatusCode);

        Instant from = _factory.Clock.GetCurrentInstant();
        Instant to = from + Duration.FromDays(7);
        HttpResponseMessage calendar = await session.GetAsync(
            $"/api/v1/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}");
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(calendar);

        Assert.Empty(body.RootElement.GetProperty("myAvailability").GetProperty("series").EnumerateArray());
    }

    private static LocalDate NextTuesday(LocalDate onOrAfter) =>
        onOrAfter.DayOfWeek == IsoDayOfWeek.Tuesday ? onOrAfter : onOrAfter.Next(IsoDayOfWeek.Tuesday);

    private static string Iso(Instant instant) => InstantPattern.ExtendedIso.Format(instant);
}
