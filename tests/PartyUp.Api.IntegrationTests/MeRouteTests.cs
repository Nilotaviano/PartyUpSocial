using System.Net;
using System.Net.Http.Json;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class MeRouteTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ChangeTimeZone_WithAnInvalidZone_Returns400()
    {
        UserId userId = await _factory.SeedUserAsync();
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        HttpResponseMessage signIn = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        string authCookie = signIn.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];

        HttpResponseMessage response = await AntiforgeryTestClient.SendAsync(
            client,
            HttpMethod.Put,
            "/api/v1/me/time-zone",
            JsonContent.Create(new { timeZone = "Not/AZone" }),
            authCookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("invalid_time_zone", problem.Code);
    }
}
