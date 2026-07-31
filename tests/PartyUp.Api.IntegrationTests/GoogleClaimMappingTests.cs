using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class GoogleClaimMappingTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new(googleEnabled: true);

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void Callback_ProjectsEmailVerifiedOntoThePrincipal_D067()
    {
        GoogleOptions options = _factory.Services
            .GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(GoogleDefaults.AuthenticationScheme);
        using JsonDocument userInfo = JsonDocument.Parse(
            """{"sub":"google-sub","email":"player@example.test","email_verified":true}""");
        var identity = new ClaimsIdentity(GoogleDefaults.AuthenticationScheme);

        foreach (ClaimAction action in options.ClaimActions)
        {
            action.Run(userInfo.RootElement, identity, "Google");
        }

        Assert.Equal("true", identity.FindFirst("email_verified")?.Value);
    }
}
