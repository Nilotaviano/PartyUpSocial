using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
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

    private readonly PartyUpApiFactory _factory = new(googleEnabled: true);

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
    public async Task AnonymousExternalIntent_IsBoundedByIntentIp()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        for (int attempt = 0; attempt < 30; attempt++)
        {
            HttpResponseMessage accepted = await AntiforgeryTestClient.PostAsync(
                client,
                "/api/v1/auth/external/intent",
                JsonContent.Create(new
                {
                    provider = "google",
                    mode = "login",
                    returnTo = "/app",
                    ageGateAttested = true,
                }));
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        HttpResponseMessage limited = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/external/intent",
            JsonContent.Create(new
            {
                provider = "google",
                mode = "login",
                returnTo = "/app",
                ageGateAttested = true,
            }));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
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

    [Fact]
    public async Task EveryChainMember_ExhaustsOnlyItsDocumentedPartition()
    {
        await using WebApplication application = await CreateLimiterProbeAsync();
        using HttpClient client = application.GetTestClient();
        LimiterCase[] cases =
        [
            Case("auth-ip", ApiOperationIds.ConsumeMagicLink),
            Case("login-email", ApiOperationIds.Login),
            Case("register-ip", ApiOperationIds.Register),
            Case("register-email", ApiOperationIds.Register),
            Case("auth-email", ApiOperationIds.ForgotPassword),
            Case("magic-link-ip", ApiOperationIds.RequestMagicLink),
            Case("deletion-request-email", ApiOperationIds.RequestAccountDeletion),
            Case("deletion-request-ip", ApiOperationIds.RequestAccountDeletion),
            Case("deletion-steam-ip", ApiOperationIds.StartSteamDeletion),
            Case("intent-ip", ApiOperationIds.IssueExternalIntent),
            Case("email-send", ApiOperationIds.ReauthenticateByEmail),
            Case("reauth", ApiOperationIds.IssueExternalIntent),
            Case("lookup", ApiOperationIds.CheckUsernameAvailability),
            Case("default", ApiOperationIds.GetSession),
        ];

        for (int caseIndex = 0; caseIndex < cases.Length; caseIndex++)
        {
            LimiterCase testCase = cases[caseIndex];
            for (int attempt = 0; attempt < testCase.Dimension.PermitLimit; attempt++)
            {
                using HttpResponseMessage allowed = await SendProbeAsync(
                    client,
                    testCase,
                    caseIndex,
                    attempt,
                    otherKey: false);
                Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
            }

            using HttpResponseMessage rejected = await SendProbeAsync(
                client,
                testCase,
                caseIndex,
                testCase.Dimension.PermitLimit,
                otherKey: false);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);

            using HttpResponseMessage otherKey = await SendProbeAsync(
                client,
                testCase,
                caseIndex,
                0,
                otherKey: true);
            Assert.Equal(HttpStatusCode.NoContent, otherKey.StatusCode);

            if (!testCase.Dimension.CoversEveryApiOperation)
            {
                using HttpResponseMessage unrelated = await SendProbeAsync(
                    client,
                    testCase with { OperationId = ApiOperationIds.GetSession },
                    caseIndex,
                    0,
                    otherKey: false);
                Assert.Equal(HttpStatusCode.NoContent, unrelated.StatusCode);
            }
        }
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

    private static LimiterCase Case(string dimensionName, string operationId) =>
        new(
            Assert.Single(
                RateLimitPolicies.Dimensions,
                dimension => dimension.Name == dimensionName),
            operationId);

    private static async Task<WebApplication> CreateLimiterProbeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = RateLimitPolicies.CreateGlobalLimiter(
                Encoding.UTF8.GetBytes("behavioral-rate-limit-test-pepper"));
            options.OnRejected = RateLimitPolicies.OnRejectedAsync;
        });
        WebApplication application = builder.Build();
        application.UseRouting();
        application.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Probe-IP", out var ip))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
            }

            if (context.Request.Headers.TryGetValue("X-Probe-User", out var user))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, user.ToString())],
                    IdentityConstants.ApplicationScheme));
            }

            await next(context);
        });
        application.UseRateLimitEmailPartitioning();
        application.UseRateLimiter();
        string[] operationIds = RateLimitPolicies.Dimensions
            .SelectMany(dimension => dimension.OperationIds)
            .Append(ApiOperationIds.GetSession)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (string operationId in operationIds)
        {
            application.MapPost(
                    $"/probe/{operationId}",
                    () => Results.NoContent())
                .WithName(operationId);
        }

        await application.StartAsync();
        return application;
    }

    private static Task<HttpResponseMessage> SendProbeAsync(
        HttpClient client,
        LimiterCase testCase,
        int caseIndex,
        int attempt,
        bool otherKey)
    {
        string keySuffix = otherKey ? "other" : "primary";
        string ip = testCase.Dimension.PartitionKind == RateLimitPartitionKind.Ip
            ? $"192.0.2.{caseIndex + (otherKey ? 101 : 1)}"
            : $"198.51.{caseIndex + 1}.{attempt + (otherKey ? 101 : 1)}";
        string email = testCase.Dimension.PartitionKind == RateLimitPartitionKind.Email
            ? $"{keySuffix}-{caseIndex}@example.test"
            : $"attempt-{caseIndex}-{attempt}-{keySuffix}@example.test";
        string user = testCase.Dimension.PartitionKind == RateLimitPartitionKind.User
            ? $"{keySuffix}-user-{caseIndex}"
            : $"attempt-user-{caseIndex}-{attempt}-{keySuffix}";
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/probe/{testCase.OperationId}")
        {
            Content = JsonContent.Create(new { email }),
        };
        request.Headers.Add("X-Probe-IP", ip);
        if (testCase.Dimension.PartitionKind == RateLimitPartitionKind.User ||
            testCase.Dimension.Name == "email-send")
        {
            request.Headers.Add("X-Probe-User", user);
        }

        return client.SendAsync(request);
    }

    private sealed record LimiterCase(
        RateLimitDimension Dimension,
        string OperationId);

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
