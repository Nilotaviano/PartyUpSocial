using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Infrastructure.Persistence;

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
    public async Task TargetRoutes_HiddenAndMissingHaveEquivalentResponsesAndEffects_D065()
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
        string missingCode = blocker.FriendCode == "2222222222"
            ? "3333333333"
            : "2222222222";
        GraphSnapshot baseline = await SnapshotAsync();

        HttpResponseMessage hiddenProfile = await blockedSession.GetAsync(
            $"/api/v1/users/by-username/{blocker.Username}");
        HttpResponseMessage missingProfile = await blockedSession.GetAsync(
            $"/api/v1/users/by-username/{missing}");
        await AssertEquivalentProblemsAsync(hiddenProfile, missingProfile, "user_not_found");

        HttpResponseMessage hiddenCode = await blockedSession.GetAsync(
            $"/api/v1/friend-codes/{blocker.FriendCode}");
        HttpResponseMessage missingCodeResponse = await blockedSession.GetAsync(
            $"/api/v1/friend-codes/{missingCode}");
        await AssertEquivalentProblemsAsync(
            hiddenCode,
            missingCodeResponse,
            "user_not_found");

        HttpResponseMessage hiddenSend = await blockedSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = blocker.Username }));
        HttpResponseMessage missingSend = await blockedSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/friend-requests",
            JsonContent.Create(new { targetUsername = missing }));
        await AssertEquivalentProblemsAsync(hiddenSend, missingSend, "user_not_found");

        HttpResponseMessage hiddenBlock = await blockedSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/blocks",
            JsonContent.Create(new { targetUsername = blocker.Username }));
        HttpResponseMessage missingBlock = await blockedSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/blocks",
            JsonContent.Create(new { targetUsername = missing }));
        await AssertEquivalentProblemsAsync(hiddenBlock, missingBlock, "user_not_found");
        Assert.NotEqual(HttpStatusCode.Forbidden, hiddenProfile.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, hiddenCode.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, hiddenSend.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, hiddenBlock.StatusCode);

        using JsonDocument requests = await FriendEndpointTest.SuccessJsonAsync(
            await blockedSession.GetAsync("/api/v1/friend-requests"));
        Assert.Empty(requests.RootElement.GetProperty("incoming").EnumerateArray());
        Assert.Empty(requests.RootElement.GetProperty("outgoing").EnumerateArray());
        AssertSnapshotEqual(baseline, await SnapshotAsync());
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("reject")]
    [InlineData("cancel")]
    [InlineData("unfriend")]
    public async Task StaleActionAfterBlock_EqualsMissingIdAndChangesNothing_D065(
        string action)
    {
        FriendEndpointUser actor = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser counterpart = await FriendEndpointTest.SeedAsync(_factory);
        using FriendEndpointSession actorSession = await FriendEndpointTest.SignInAsync(
            _factory,
            actor.Id);
        using FriendEndpointSession counterpartSession = await FriendEndpointTest.SignInAsync(
            _factory,
            counterpart.Id);

        bool incomingAction = action is "accept" or "reject";
        FriendEndpointSession requesterSession = incomingAction
            ? counterpartSession
            : actorSession;
        string addresseeUsername = incomingAction
            ? actor.Username
            : counterpart.Username;
        using JsonDocument pending = await FriendEndpointTest.SuccessJsonAsync(
            await requesterSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/friend-requests",
                JsonContent.Create(new { targetUsername = addresseeUsername })));
        Guid staleId = pending.RootElement.GetProperty("friendshipId").GetGuid();

        if (action == "unfriend")
        {
            HttpResponseMessage accepted = await counterpartSession.SendUnsafeAsync(
                HttpMethod.Post,
                $"/api/v1/friend-requests/{staleId:D}/accept");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        HttpResponseMessage blocked = await actorSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/blocks",
            JsonContent.Create(new { targetUsername = counterpart.Username }));
        Assert.Equal(HttpStatusCode.Created, blocked.StatusCode);
        GraphSnapshot baseline = await SnapshotAsync();

        (HttpMethod method, string stalePath, string missingPath, string code) = action switch
        {
            "accept" => (
                HttpMethod.Post,
                $"/api/v1/friend-requests/{staleId:D}/accept",
                $"/api/v1/friend-requests/{Guid.NewGuid():D}/accept",
                "friend_request_not_found"),
            "reject" => (
                HttpMethod.Post,
                $"/api/v1/friend-requests/{staleId:D}/reject",
                $"/api/v1/friend-requests/{Guid.NewGuid():D}/reject",
                "friend_request_not_found"),
            "cancel" => (
                HttpMethod.Delete,
                $"/api/v1/friend-requests/{staleId:D}",
                $"/api/v1/friend-requests/{Guid.NewGuid():D}",
                "friend_request_not_found"),
            "unfriend" => (
                HttpMethod.Delete,
                $"/api/v1/friends/{staleId:D}",
                $"/api/v1/friends/{Guid.NewGuid():D}",
                "friendship_not_found"),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        HttpResponseMessage hidden = await actorSession.SendUnsafeAsync(method, stalePath);
        AssertSnapshotEqual(baseline, await SnapshotAsync());
        HttpResponseMessage missing = await actorSession.SendUnsafeAsync(method, missingPath);
        await AssertEquivalentProblemsAsync(hidden, missing, code);
        AssertSnapshotEqual(baseline, await SnapshotAsync());
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

    private async Task<GraphSnapshot> SnapshotAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var friendships = await context.Friendships.AsNoTracking().ToArrayAsync();
        var blocks = await context.Blocks.AsNoTracking().ToArrayAsync();
        var notifications = await context.Notifications.AsNoTracking().ToArrayAsync();
        return new GraphSnapshot(
            friendships.Select(friendship =>
                    $"{friendship.Id.Value:D}|{friendship.UserLoId.Value:D}|" +
                    $"{friendship.UserHiId.Value:D}|{friendship.RequestedBy.Value:D}|" +
                    $"{friendship.Status}|{friendship.RequestedAt.ToUnixTimeTicks()}|" +
                    $"{friendship.AcceptedAt?.ToUnixTimeTicks()}")
                .Order(StringComparer.Ordinal)
                .ToArray(),
            blocks.Select(block =>
                    $"{block.Id.Value:D}|{block.BlockerId.Value:D}|" +
                    $"{block.BlockedId.Value:D}|{block.CreatedAt.ToUnixTimeTicks()}")
                .Order(StringComparer.Ordinal)
                .ToArray(),
            notifications.Select(notification =>
                    $"{notification.Id.Value:D}|{notification.RecipientUserId.Value:D}|" +
                    $"{notification.Type}|{notification.SessionId?.Value:D}|" +
                    $"{notification.PollId?.Value:D}|{notification.ActorUserId?.Value:D}|" +
                    $"{notification.OptionStartTime?.ToUnixTimeTicks()}|" +
                    $"{notification.CreatedAt.ToUnixTimeTicks()}|" +
                    $"{notification.ReadAt?.ToUnixTimeTicks()}")
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static void AssertSnapshotEqual(GraphSnapshot expected, GraphSnapshot actual)
    {
        Assert.Equal<string>(expected.Friendships, actual.Friendships);
        Assert.Equal<string>(expected.Blocks, actual.Blocks);
        Assert.Equal<string>(expected.Notifications, actual.Notifications);
    }

    private sealed record GraphSnapshot(
        string[] Friendships,
        string[] Blocks,
        string[] Notifications);
}
