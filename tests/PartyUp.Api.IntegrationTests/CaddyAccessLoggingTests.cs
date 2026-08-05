using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class CaddyAccessLoggingTests
{
    [Fact]
    public async Task IdentifierRoutes_AreRedactedInCapturedAccessEvents_D031()
    {
        string caddyfile = Path.Combine(FindRepositoryRoot(), "deploy", "Caddyfile");
        await using IContainer caddy = new ContainerBuilder("caddy:2")
            .WithPortBinding(80, assignRandomHostPort: true)
            .WithBindMount(caddyfile, "/etc/caddy/Caddyfile", AccessMode.ReadOnly)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("serving initial configuration"))
            .Build();
        await caddy.StartAsync();

        string[] markers =
        [
            "proxy-spa-user-marker",
            "proxy-spa-code-marker",
            "proxy-api-user-marker",
            "proxy-api-code-marker",
        ];
        string[] routes =
        [
            $"/U/{markers[0]}/malformed-extra?secret={markers[0]}",
            $"/AdD/{markers[1]}%2Fencoded?secret={markers[1]}",
            $"/API/V1/Users/By-Username/{markers[2]}/malformed-extra?secret={markers[2]}",
            $"/Api/v1/Friend-Codes/{markers[3]}%252Fencoded?secret={markers[3]}",
        ];
        string[] expectedUris =
        [
            "/U/REDACTED",
            "/AdD/REDACTED",
            "/API/V1/Users/By-Username/REDACTED",
            "/Api/v1/Friend-Codes/REDACTED",
        ];
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(
                $"http://{caddy.Hostname}:{caddy.GetMappedPublicPort(80)}"),
        };

        foreach (string route in routes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Host = "partyup.social";
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        }

        await Task.Delay(250);
        (string stdout, string stderr) = await caddy.GetLogsAsync(
            DateTime.UtcNow.AddMinutes(-2),
            DateTime.UtcNow.AddMinutes(1),
            timestampsEnabled: false);
        string combined = stdout + Environment.NewLine + stderr;
        JsonElement[] accessEvents = combined
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(TryParse)
            .Where(element => element is { } &&
                element.Value.TryGetProperty("request", out _))
            .Select(element => element!.Value)
            .ToArray();

        Assert.Equal(expectedUris, accessEvents.Select(AccessUri));
        foreach (string marker in markers)
        {
            Assert.DoesNotContain(marker, combined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Uri.EscapeDataString(marker),
                combined,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// PHASE4_DESIGN.md §9.2/§9.1: a guest bearer link is redacted from the access log the same way
    /// as the other identifier-bearing routes, and the request's <c>Referer</c> header — the vector a
    /// browser navigation away from <c>/g/&lt;token&gt;</c> could otherwise use to leak the token to a
    /// third party — is stripped entirely from the captured event, never merely masked.
    /// </summary>
    [Fact]
    public async Task GuestExchangeRoute_IsRedactedAndRefererIsStrippedFromAccessEvents_D031_A044()
    {
        string caddyfile = Path.Combine(FindRepositoryRoot(), "deploy", "Caddyfile");
        await using IContainer caddy = new ContainerBuilder("caddy:2")
            .WithPortBinding(80, assignRandomHostPort: true)
            .WithBindMount(caddyfile, "/etc/caddy/Caddyfile", AccessMode.ReadOnly)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("serving initial configuration"))
            .Build();
        await caddy.StartAsync();

        const string guestTokenMarker = "guest-bearer-token-marker";
        const string refererMarker = "referer-leaked-token-marker";

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://{caddy.Hostname}:{caddy.GetMappedPublicPort(80)}"),
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/g/{guestTokenMarker}");
        request.Headers.Host = "partyup.social";
        request.Headers.Referrer = new Uri($"https://partyup.social/g/{refererMarker}");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);

        await Task.Delay(250);
        (string stdout, string stderr) = await caddy.GetLogsAsync(
            DateTime.UtcNow.AddMinutes(-2),
            DateTime.UtcNow.AddMinutes(1),
            timestampsEnabled: false);
        string combined = stdout + Environment.NewLine + stderr;
        JsonElement[] accessEvents = combined
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(TryParse)
            .Where(element => element is { } &&
                element.Value.TryGetProperty("request", out _))
            .Select(element => element!.Value)
            .ToArray();

        JsonElement guestEvent = Assert.Single(accessEvents);
        Assert.Equal("/g/REDACTED", AccessUri(guestEvent));

        // The Referer field is deleted outright by the log pipeline — not merely redacted — so it
        // must not appear at all under the event's headers.
        if (guestEvent.GetProperty("request").TryGetProperty("headers", out JsonElement headers))
        {
            Assert.False(headers.TryGetProperty("Referer", out _));
        }

        Assert.DoesNotContain(guestTokenMarker, combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(refererMarker, combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Uri.EscapeDataString(refererMarker),
            combined,
            StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement? TryParse(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string AccessUri(JsonElement accessEvent) =>
        accessEvent.GetProperty("request").GetProperty("uri").GetString()!;

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFile = "")
    {
        foreach (string candidate in new[]
                 {
                     Path.GetDirectoryName(sourceFile) ?? string.Empty,
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory,
                 })
        {
            DirectoryInfo? directory = new(candidate);
            while (directory is not null &&
                   !File.Exists(Path.Combine(directory.FullName, "PartyUp.sln")))
            {
                directory = directory.Parent;
            }

            if (directory is not null)
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
