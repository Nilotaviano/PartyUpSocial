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
    private static readonly string[] ExpectedOwnerProperties = ["displayName", "ordinal"];

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

    [Fact]
    public async Task PollLayerIsDeclaredAndEmpty_D111()
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

        // Every declared layer is present on the one atomic response (A-080).
        Assert.True(body.RootElement.TryGetProperty("myAvailability", out _));
        Assert.True(body.RootElement.TryGetProperty("friendAvailability", out _));
        Assert.True(body.RootElement.TryGetProperty("sessions", out _));
    }

    private static string Iso(Instant instant) => InstantPattern.ExtendedIso.Format(instant);
}
