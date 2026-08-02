using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PartyUp.Application.Abstractions;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class SteamFriendListClientTests
{
    [Fact]
    public async Task Get_MapsPrivateEmptyFailureAndMalformedResponses_D049()
    {
        SteamFriendListResult privateResult = await ExecuteAsync(request =>
            request.RequestUri!.AbsolutePath.Contains("GetFriendList", StringComparison.Ordinal)
                ? Response(HttpStatusCode.Unauthorized)
                : Json("""{"response":{"players":[{"steamid":"importer"}]}}"""));
        Assert.IsType<SteamFriendListPrivateProfile>(privateResult);

        SteamFriendListResult empty = await ExecuteAsync(_ =>
            Json("""{"friendslist":{"friends":[]}}"""));
        Assert.Empty(Assert.IsType<SteamFriendListSuccess>(empty).Friends);

        SteamFriendListResult failure = await ExecuteAsync(_ =>
            Response(HttpStatusCode.BadGateway));
        Assert.IsType<SteamFriendListUpstreamFailure>(failure);

        SteamFriendListResult malformed = await ExecuteAsync(_ => Json("42"));
        Assert.IsType<SteamFriendListUpstreamFailure>(malformed);
    }

    [Fact]
    public async Task Get_DisambiguatesInvalidKeyFromPrivateListWithPlayerSummaryProbe_D049()
    {
        SteamFriendListResult validKey = await ExecuteAsync(request =>
            request.RequestUri!.AbsolutePath.Contains("GetFriendList", StringComparison.Ordinal)
                ? Response(HttpStatusCode.Unauthorized)
                : Json("""{"response":{"players":[{"steamid":"importer"}]}}"""));
        SteamFriendListResult invalidKey = await ExecuteAsync(request =>
            request.RequestUri!.AbsolutePath.Contains("GetFriendList", StringComparison.Ordinal)
                ? Response(HttpStatusCode.Unauthorized)
                : Response(HttpStatusCode.Forbidden));
        SteamFriendListResult wrongProbeIdentity = await ExecuteAsync(request =>
            request.RequestUri!.AbsolutePath.Contains("GetFriendList", StringComparison.Ordinal)
                ? Response(HttpStatusCode.Unauthorized)
                : Json("""{"response":{"players":[{"steamid":"someone-else"}]}}"""));

        Assert.IsType<SteamFriendListPrivateProfile>(validKey);
        Assert.IsType<SteamFriendListUpstreamFailure>(invalidKey);
        Assert.IsType<SteamFriendListUpstreamFailure>(wrongProbeIdentity);
    }

    [Fact]
    public async Task Get_EnrichmentFailureReturnsSuccessWithNullPersonaAndAvatar_D049()
    {
        SteamFriendListResult result = await ExecuteAsync(request =>
            request.RequestUri!.AbsolutePath.Contains("GetFriendList", StringComparison.Ordinal)
                ? Json("""{"friendslist":{"friends":[{"steamid":"friend-1"}]}}""")
                : Json("not-json"));

        SteamFriend friend = Assert.Single(Assert.IsType<SteamFriendListSuccess>(result).Friends);
        Assert.Equal("friend-1", friend.SteamId64);
        Assert.Null(friend.PersonaName);
        Assert.Null(friend.AvatarUrl);
    }

    [Fact]
    public async Task Get_ScrubsTheApiKeyFromLogsAndExceptions_D049()
    {
        const string apiKey = "never-log-this-key";
        var logger = new CaptureLogger<SteamApiClient>();
        using var http = new HttpClient(new DelegateHttpHandler(_ =>
            Response(HttpStatusCode.InternalServerError)));
        var api = new SteamApiClient(http, logger);
        Uri requestUri = new(
            $"https://api.steampowered.com/test?key={apiKey}&steamid=importer");

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.GetStringAsync(requestUri, default));

        Assert.DoesNotContain(apiKey, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("key=[redacted]", exception.Message, StringComparison.Ordinal);
        Assert.All(
            logger.Messages,
            message => Assert.DoesNotContain(apiKey, message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = new HttpClient(new DelegateHttpHandler(_ =>
            throw new OperationCanceledException(cancellation.Token)));
        var client = new SteamFriendListClient(
            new SteamApiClient(http, NullLogger<SteamApiClient>.Instance),
            "test-key");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync("importer", cancellation.Token));
    }

    private static async Task<SteamFriendListResult> ExecuteAsync(
        Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        using var http = new HttpClient(new DelegateHttpHandler(response));
        var client = new SteamFriendListClient(
            new SteamApiClient(http, NullLogger<SteamApiClient>.Instance),
            "test-key");
        return await client.GetAsync("importer", default);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Response(HttpStatusCode status) => new(status);
}

internal sealed class DelegateHttpHandler(
    Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(response(request));
}

internal sealed class CaptureLogger<T> : ILogger<T>
{
    internal List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}
