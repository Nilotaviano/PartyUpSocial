using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class FriendEndpointProblemTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task UnsafeRoute_RejectsMissingCsrfWithExactProblem()
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser target = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession session = await FriendEndpointTest.SignInAsync(
            _factory,
            actor.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/blocks")
        {
            Content = JsonContent.Create(new { targetUsername = target.Username }),
        };
        request.Headers.Add(HeaderNames.Cookie, session.AuthCookie);

        HttpResponseMessage response = await session.Client.SendAsync(request);

        await FriendEndpointTest.AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "antiforgery_failed",
            "/api/v1/blocks");
    }

    [Fact]
    public async Task BlockHiddenAndMissingTargets_HaveEquivalentProfileAndSendResponses()
    {
        FriendEndpointUser blocker = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser blocked = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession blockerSession = await FriendEndpointTest.SignInAsync(
            _factory,
            blocker.Id);
        HttpResponseMessage block = await blockerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/blocks",
            JsonContent.Create(new { targetUsername = blocked.Username }));
        Assert.Equal(HttpStatusCode.Created, block.StatusCode);

        using FriendEndpointSession blockedSession = await FriendEndpointTest.SignInAsync(
            _factory,
            blocked.Id);
        string missing = "miss" + blocker.Username[4..];
        Assert.Equal(blocker.Username.Length, missing.Length);

        HttpResponseMessage hiddenProfile = await blockedSession.GetAsync(
            $"/api/v1/users/by-username/{blocker.Username}");
        HttpResponseMessage missingProfile = await blockedSession.GetAsync(
            $"/api/v1/users/by-username/{missing}");
        await AssertEquivalentProblemsAsync(hiddenProfile, missingProfile, "user_not_found");

        HttpResponseMessage hiddenSend = await blockedSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = blocker.Username }));
        HttpResponseMessage missingSend = await blockedSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = missing }));
        await AssertEquivalentProblemsAsync(hiddenSend, missingSend, "user_not_found");
        Assert.NotEqual(HttpStatusCode.Forbidden, hiddenProfile.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, hiddenSend.StatusCode);

        using JsonDocument requests = await FriendEndpointTest.SuccessJsonAsync(
            await blockedSession.GetAsync("/api/v1/friend-requests"));
        Assert.Empty(requests.RootElement.GetProperty("incoming").EnumerateArray());
        Assert.Empty(requests.RootElement.GetProperty("outgoing").EnumerateArray());
    }

    private static async Task AssertEquivalentProblemsAsync(
        HttpResponseMessage hidden,
        HttpResponseMessage missing,
        string expectedCode)
    {
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(hidden.StatusCode, missing.StatusCode);
        Assert.Equal(HeaderSnapshot(hidden), HeaderSnapshot(missing));
        Assert.Equal(
            await NormalizedProblemAsync(hidden, expectedCode),
            await NormalizedProblemAsync(missing, expectedCode));
    }

    private static Dictionary<string, string> HeaderSnapshot(
        HttpResponseMessage response) =>
        response.Headers
            .Concat(response.Content.Headers)
            .Where(header => !header.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);

    private static async Task<string> NormalizedProblemAsync(
        HttpResponseMessage response,
        string expectedCode)
    {
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        Assert.Equal(6, root.EnumerateObject().Count());
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        return JsonSerializer.Serialize(new
        {
            type = root.GetProperty("type").GetString(),
            title = root.GetProperty("title").GetString(),
            status = root.GetProperty("status").GetInt32(),
            code = root.GetProperty("code").GetString(),
        });
    }
}
