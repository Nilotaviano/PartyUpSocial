using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Auth;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ExternalStateTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Callback_WithoutTheStateCookie_IsRejected()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/auth/external/steam/callback?n=nonce");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("state_mismatch", problem.Code);
    }

    [Fact]
    public async Task DeletionStateCookie_IsRejectedByTheAuthCallback()
    {
        IDataProtectionProvider protection =
            _factory.Services.GetRequiredService<IDataProtectionProvider>();
        string value = protection.CreateProtector("PartyUp.SteamState.Deletion")
            .Protect(JsonSerializer.Serialize(new { Flow = "deletion", Nonce = "nonce" }));
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/auth/external/steam/callback?n=nonce");
        request.Headers.Add(HeaderNames.Cookie, $"pu.ext={value}");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("state_mismatch", problem.Code);
    }

    [Fact]
    public async Task AuthPurposeCookie_ForTheDeletionFlow_IsRejectedByTheAuthCallback()
    {
        IDataProtectionProvider protection =
            _factory.Services.GetRequiredService<IDataProtectionProvider>();
        string value = protection.CreateProtector("PartyUp.SteamState.Auth")
            .Protect(JsonSerializer.Serialize(new
            {
                Flow = "deletion",
                Nonce = "nonce",
                Mode = ExternalStartMode.Login,
                ReturnTo = "/app",
                BoundUserId = (Guid?)null,
                AgeGateAttested = true,
            }));
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/auth/external/steam/callback?n=nonce");
        request.Headers.Add(HeaderNames.Cookie, $"pu.ext={value}");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("state_mismatch", problem.Code);
    }
}
