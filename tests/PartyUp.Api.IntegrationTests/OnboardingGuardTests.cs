using System.Net;
using Microsoft.Net.Http.Headers;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class OnboardingGuardTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ProtectedEndpoint_WhileOnboarding_Returns403_D127()
    {
        UserId userId = await _factory.SeedUserAsync(completeProfile: false);

        HttpResponseMessage response = await RequestOnboardedEndpointAsync(userId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_ForAnIncompleteProfileInPendingDeletion_Returns403_D127()
    {
        UserId userId = await _factory.SeedUserAsync(
            completeProfile: false,
            pendingDeletion: true);

        HttpResponseMessage response = await RequestOnboardedEndpointAsync(userId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<HttpResponseMessage> RequestOnboardedEndpointAsync(UserId userId)
    {
        using HttpClient client = _factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        HttpResponseMessage login = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        string setCookie = login.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/_slice2/onboarded");
        request.Headers.Add(HeaderNames.Cookie, setCookie.Split(';', 2)[0]);
        return await client.SendAsync(request);
    }
}
