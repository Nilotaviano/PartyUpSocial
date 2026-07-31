using System.Net;
using System.Net.Http.Json;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AntiforgeryTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Post_WithoutTheHeader_Fails()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/_slice3/rate-limit/login",
            new { email = "player@example.test" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("antiforgery_failed", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_WithTheHeader_Succeeds()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/_slice3/rate-limit/login",
            JsonContent.Create(new { email = "player@example.test" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_ReturnsTheRequestTokenInTheBody_AndTheCookieIsHttpOnly()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/antiforgery");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AntiforgeryTokenResponse body =
            (await response.Content.ReadFromJsonAsync<AntiforgeryTokenResponse>())!;
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        SetCookieHeaderValue cookie = response.Headers.GetValues(HeaderNames.SetCookie)
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(value => value.Name.Value == "pu.csrf");
        Assert.True(cookie.HttpOnly);
        Assert.NotEqual(cookie.Value.Value, body.Token);
    }

    private HttpClient CreateClient() => _factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
}
