using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PartyUp.Application.Auth;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(RequestLoggingTestGroup.Name)]
public sealed class RequestLoggingTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Log_NeverContainsAQueryString()
    {
        var sink = new CapturingSink();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSerilog((provider, logger) => logger
                .ReadFrom.Configuration(provider.GetRequiredService<IConfiguration>())
                .WriteTo.Sink(sink))));
        using HttpClient client = factory.CreateClient();

        _ = await client.GetAsync("/auth/magic?token=x");

        LogEvent[] requestLogs = sink.Events.Where(logEvent =>
            logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? source) &&
            source is ScalarValue { Value: "Serilog.AspNetCore.RequestLoggingMiddleware" } &&
            logEvent.Properties.TryGetValue("RequestPath", out LogEventPropertyValue? path) &&
            path is ScalarValue { Value: "/auth/magic" }).ToArray();
        Assert.NotEmpty(requestLogs);
        Assert.DoesNotContain(sink.Events, logEvent =>
            logEvent.RenderMessage(CultureInfo.InvariantCulture).Contains(
                "token=x",
                StringComparison.Ordinal));
        Assert.All(requestLogs, requestLog =>
            Assert.DoesNotContain(
                "?",
                requestLog.Properties["RequestPath"].ToString(),
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Log_NoStructuredPropertyContainsAnyQueryStringOrRawToken()
    {
        const string ApiSecret = "api-query-token-a044";
        const string SpaSecret = "spa-query-token-a044";
        const string MalformedSecret = "malformed-query-token-a044";
        const string ExceptionSecret = "exception-query-token-a044";
        string[] secrets = [ApiSecret, SpaSecret, MalformedSecret, ExceptionSecret];
        var sink = new CapturingSink();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISecurityTokenService>();
                services.AddScoped<ISecurityTokenService, ThrowingTokenService>();
                services.AddSerilog((provider, logger) => logger
                    .ReadFrom.Configuration(provider.GetRequiredService<IConfiguration>())
                    .WriteTo.Sink(sink));
            }));
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false,
            });

        _ = await client.GetAsync($"/api/v1/auth/session?token={ApiSecret}");
        _ = await client.GetAsync($"/auth/magic?token={SpaSecret}");
        _ = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/auth/login?token={MalformedSecret}",
            new StringContent("{", Encoding.UTF8, "application/json"));
        _ = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/auth/magic-link/consume?token={ExceptionSecret}",
            JsonContent.Create(new { token = "not-the-query-token", ageGateAttested = true }));

        Assert.NotEmpty(sink.Events);
        Assert.Contains(sink.Events, logEvent => logEvent.Exception is not null);
        foreach (LogEvent logEvent in sink.Events)
        {
            AssertContainsNoSecret(
                logEvent.RenderMessage(CultureInfo.InvariantCulture),
                secrets);
            foreach ((string name, LogEventPropertyValue value) in logEvent.Properties)
            {
                AssertContainsNoSecret(name, secrets);
                foreach (string propertyValue in Flatten(value))
                {
                    AssertContainsNoSecret(propertyValue, secrets);
                }
            }

            if (logEvent.Exception is not null)
            {
                foreach (string exceptionValue in Flatten(logEvent.Exception))
                {
                    AssertContainsNoSecret(exceptionValue, secrets);
                }
            }
        }

        // Static text check only: the query filter's wildcard-looking form
        // silently no-ops (Caddy reads the asterisk as a literal parameter
        // name), so the assertions pin the regexp form that was empirically
        // proven to scrub against the pinned caddy:2 image, and reject any
        // reappearance of the no-op form.
        string caddyfile = File.ReadAllText(FindRepositoryFile(["deploy", "Caddyfile"]));
        Assert.Contains("request>uri multi_regexp", caddyfile, StringComparison.Ordinal);
        Assert.Contains(@"""\?.*""", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("delete *", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FriendIdentifierRoutes_EmitOnlyRedactedRequestPath_D031()
    {
        string[] markers =
        [
            "spa-username-marker",
            "spa-code-marker",
            "api-username-marker",
            "api-code-marker",
        ];
        string[] routes =
        [
            $"/u/{markers[0]}/malformed-extra",
            $"/add/{markers[1]}%2Fencoded",
            $"/api/v1/users/by-username/{markers[2]}/malformed-extra",
            $"/api/v1/friend-codes/{markers[3]}%252Fencoded",
        ];
        string[] expectedPaths =
        [
            "/u/REDACTED",
            "/add/REDACTED",
            "/api/v1/users/by-username/REDACTED",
            "/api/v1/friend-codes/REDACTED",
        ];
        var sink = new CapturingSink();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSerilog((provider, logger) => logger
                .ReadFrom.Configuration(provider.GetRequiredService<IConfiguration>())
                .WriteTo.Sink(sink))));
        using HttpClient client = factory.CreateClient();

        foreach (string route in routes)
        {
            _ = await client.GetAsync(route);
        }

        LogEvent[] requestLogs = sink.Events.Where(logEvent =>
            logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? source) &&
            source is ScalarValue { Value: "Serilog.AspNetCore.RequestLoggingMiddleware" }).ToArray();
        Assert.Equal(4, requestLogs.Length);
        Assert.Equal(
            expectedPaths,
            requestLogs.Select(logEvent =>
                Assert.IsType<ScalarValue>(logEvent.Properties["RequestPath"]).Value));

        foreach (LogEvent logEvent in sink.Events)
        {
            AssertContainsNoSecret(
                logEvent.RenderMessage(CultureInfo.InvariantCulture),
                markers);
            foreach (LogEventPropertyValue value in logEvent.Properties.Values)
            {
                foreach (string propertyValue in Flatten(value))
                {
                    AssertContainsNoSecret(propertyValue, markers);
                }
            }
        }
    }

    private static IEnumerable<string> Flatten(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue scalar:
                yield return Convert.ToString(
                    scalar.Value,
                    CultureInfo.InvariantCulture) ?? string.Empty;
                break;
            case SequenceValue sequence:
                foreach (LogEventPropertyValue element in sequence.Elements)
                {
                    foreach (string nested in Flatten(element))
                    {
                        yield return nested;
                    }
                }
                break;
            case StructureValue structure:
                if (structure.TypeTag is not null)
                {
                    yield return structure.TypeTag;
                }
                foreach (LogEventProperty property in structure.Properties)
                {
                    yield return property.Name;
                    foreach (string nested in Flatten(property.Value))
                    {
                        yield return nested;
                    }
                }
                break;
            case DictionaryValue dictionary:
                foreach (KeyValuePair<ScalarValue, LogEventPropertyValue> pair in dictionary.Elements)
                {
                    foreach (string key in Flatten(pair.Key))
                    {
                        yield return key;
                    }
                    foreach (string nested in Flatten(pair.Value))
                    {
                        yield return nested;
                    }
                }
                break;
        }
    }

    private static IEnumerable<string> Flatten(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current.Message;
            yield return current.StackTrace ?? string.Empty;
            yield return current.Source ?? string.Empty;
            foreach (System.Collections.DictionaryEntry entry in current.Data)
            {
                yield return Convert.ToString(
                    entry.Key,
                    CultureInfo.InvariantCulture) ?? string.Empty;
                yield return Convert.ToString(
                    entry.Value,
                    CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
    }

    private static void AssertContainsNoSecret(string value, IEnumerable<string> secrets)
    {
        foreach (string secret in secrets)
        {
            Assert.DoesNotContain(secret, value, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryFile(
        IReadOnlyList<string> segments,
        [CallerFilePath] string sourceFilePath = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file {Path.Combine([.. segments])}.");
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }

    private sealed class ThrowingTokenService : ISecurityTokenService
    {
        public Task<IssuedToken> IssueAsync(
            IssueTokenRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<IssuedToken>(
                new InvalidOperationException("Synthetic token-service failure."));

        public Task<TokenRedemption> RedeemAsync(
            RedeemTokenRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<TokenRedemption>(
                new InvalidOperationException("Synthetic token-service failure."));
    }
}

[Trait("Category", "Integration")]
public sealed class Phase2ResponseParityTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new ResponseOnlyFactory();

    [Fact]
    public async Task ExistingPhase2Failure_RetainsExact500Problem()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/magic-link/consume",
            JsonContent.Create(new { token = "phase2-500", ageGateAttested = true }));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);
        string traceId = problem.RootElement.GetProperty("traceId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(traceId));
        Assert.Equal(
            $$"""{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.1","title":"An error occurred while processing your request.","status":500,"traceId":"{{traceId}}"}""",
            body);
    }

    [Fact]
    public async Task ExistingPhase2Antiforgery_RetainsExactProblemBody()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "player@example.test", password = "wrong-password" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);
        Assert.Equal(
            ["type", "title", "status", "detail", "instance", "code", "traceId"],
            problem.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("about:blank", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal("Bad Request", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "The antiforgery token is invalid.",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(
            "/api/v1/auth/login",
            problem.RootElement.GetProperty("instance").GetString());
        Assert.Equal("antiforgery_failed", problem.RootElement.GetProperty("code").GetString());
        string traceId = problem.RootElement.GetProperty("traceId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(traceId));
        Assert.Equal(
            $$"""{"type":"about:blank","title":"Bad Request","status":400,"detail":"The antiforgery token is invalid.","instance":"/api/v1/auth/login","code":"antiforgery_failed","traceId":"{{traceId}}"}""",
            body);
    }

    public void Dispose() => _factory.Dispose();

    private sealed class ResponseOnlyFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ISecurityTokenService>();
                services.AddScoped<ISecurityTokenService, ThrowingTokenService>();
            });
        }
    }

    private sealed class ThrowingTokenService : ISecurityTokenService
    {
        public Task<IssuedToken> IssueAsync(
            IssueTokenRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<IssuedToken>(
                new InvalidOperationException("Synthetic token-service failure."));

        public Task<TokenRedemption> RedeemAsync(
            RedeemTokenRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<TokenRedemption>(
                new InvalidOperationException("Synthetic token-service failure."));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RequestLoggingTestGroup
{
    public const string Name = "Request logging";
}
