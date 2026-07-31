using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PartyUp.Api.Security;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class RateLimitTests : IAsyncLifetime, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedRoutes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["auth-ip"] = ["Login", "ConsumeMagicLink", "ResetPassword", "VerifyEmail"],
            ["login-email"] = ["Login"],
            ["register-ip"] = ["Register"],
            ["register-email"] = ["Register"],
            ["auth-email"] = ["RequestMagicLink", "ForgotPassword"],
            ["magic-link-ip"] = ["RequestMagicLink"],
            ["deletion-request-email"] = ["RequestAccountDeletion"],
            ["deletion-request-ip"] = ["RequestAccountDeletion"],
            ["deletion-steam-ip"] = ["StartSteamDeletion", "CompleteSteamDeletion"],
            ["intent-ip"] = ["IssueExternalIntent"],
            ["email-send"] = ["RequestEmailVerification", "ReauthenticateByEmail"],
            ["reauth"] =
            [
                "Reauthenticate",
                "ReauthenticateByEmail",
                "ChangePassword",
                "SetPassword",
                "IssueExternalIntent",
                "DeleteAuthMethod",
                "DeletePlatformLink",
                "ChangeEmail",
                "DeleteMe",
            ],
            ["lookup"] = ["CheckUsernameAvailability"],
            ["default"] = [],
        };

    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void EveryDimensionCoversItsDocumentedRoutes()
    {
        Assert.Equal(14, RateLimitPolicies.Dimensions.Count);
        Assert.Equal(ExpectedRoutes.Keys, RateLimitPolicies.Dimensions.Select(value => value.Name));
        foreach (RateLimitDimension dimension in RateLimitPolicies.Dimensions)
        {
            Assert.True(
                dimension.OperationIds.SetEquals(ExpectedRoutes[dimension.Name]),
                $"Unexpected route set for {dimension.Name}.");
        }

        Assert.True(RateLimitPolicies.Dimensions[^1].CoversEveryApiOperation);
        IEnumerable<EndpointDataSource> dataSources =
            _factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
        Assert.DoesNotContain(
            dataSources.SelectMany(source => source.Endpoints),
            endpoint => endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null);
    }

    [Fact]
    public async Task PartitionKey_UsesTheForwardedClientIpNotTheProxy()
    {
        await using var factory = ForwardedHeadersFactory();
        using HttpClient client = CreateClient(factory);

        for (int index = 0; index < 20; index++)
        {
            HttpResponseMessage allowed = await PostLoginProbeAsync(
                client,
                $"one-{index}@example.test",
                "192.0.2.10");
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await PostLoginProbeAsync(client, "blocked@example.test", "192.0.2.10")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await PostLoginProbeAsync(client, "other@example.test", "192.0.2.11")).StatusCode);

        for (int index = 0; index < 20; index++)
        {
            HttpResponseMessage allowed = await PostLoginProbeAsync(
                client,
                $"spoof-{index}@example.test",
                $"203.0.113.{index + 1}, 192.0.2.12");
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await PostLoginProbeAsync(
                client,
                "spoof-blocked@example.test",
                "203.0.113.250, 192.0.2.12")).StatusCode);
    }

    [Fact]
    public async Task Login_ExhaustsBothTheIpAndTheEmailCeiling()
    {
        await using var factory = ForwardedHeadersFactory();
        using HttpClient client = CreateClient(factory);

        for (int index = 0; index < 10; index++)
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await PostLoginProbeAsync(
                    client,
                    "same@example.test",
                    "192.0.2.20")).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await PostLoginProbeAsync(
                client,
                "same@example.test",
                "192.0.2.20")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await PostLoginProbeAsync(
                client,
                "different@example.test",
                "192.0.2.20")).StatusCode);

        for (int index = 0; index < 20; index++)
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await PostLoginProbeAsync(
                    client,
                    $"many-{index}@example.test",
                    "192.0.2.21")).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await PostLoginProbeAsync(
                client,
                "many-blocked@example.test",
                "192.0.2.21")).StatusCode);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
        ForwardedHeadersFactory() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Network:TrustedProxies:0"] = "10.0.0.2",
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(
                    IPAddress.Parse("10.0.0.2"))));
        });

    private static HttpClient CreateClient(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    private static Task<HttpResponseMessage> PostLoginProbeAsync(
        HttpClient client,
        string email,
        string forwardedFor) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email, password = "not-the-password" }),
            forwardedFor: forwardedFor);

    private sealed class RemoteIpStartupFilter(IPAddress proxyAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use(async (context, following) =>
                {
                    context.Connection.RemoteIpAddress = proxyAddress;
                    await following(context);
                });
                next(application);
            };
    }
}
