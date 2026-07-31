using System.Net;
using System.Net.Http.Json;
using PartyUp.Api.Endpoints;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SessionEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Session_ForAnAnonymousCaller_ListsTheConfiguredProviders()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SessionResponse session = (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
        Assert.False(session.Authenticated);
        Assert.Equal(["password", "magicLink", "steam"], session.Providers);
        Assert.DoesNotContain("google", session.Providers);
    }

    [Fact]
    public async Task Session_ListsSteamWithNoSteamApiKeyConfigured_D067()
    {
        using HttpClient client = _factory.CreateClient();

        SessionResponse enabled = (await (await client.GetAsync("/api/v1/auth/session"))
            .Content.ReadFromJsonAsync<SessionResponse>())!;
        HttpResponseMessage start = await client.GetAsync("/api/v1/auth/external/steam/start");

        Assert.Contains("steam", enabled.Providers);
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);

        await using var disabledFactory = new PartyUpApiFactory(steamEnabled: false);
        await disabledFactory.StartAsync();
        try
        {
            using HttpClient disabledClient = disabledFactory.CreateClient();
            SessionResponse disabled = (await (await disabledClient.GetAsync("/api/v1/auth/session"))
                .Content.ReadFromJsonAsync<SessionResponse>())!;
            HttpResponseMessage disabledStart = await disabledClient.GetAsync(
                "/api/v1/auth/external/steam/start");

            Assert.DoesNotContain("steam", disabled.Providers);
            Assert.Equal(HttpStatusCode.NotFound, disabledStart.StatusCode);
        }
        finally
        {
            await disabledFactory.StopAsync();
        }
    }
}
