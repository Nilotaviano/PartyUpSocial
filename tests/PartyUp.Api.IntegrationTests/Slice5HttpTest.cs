using Microsoft.Net.Http.Headers;

namespace PartyUp.Api.IntegrationTests;

internal static class Slice5HttpTest
{
    internal static HttpClient CreateClient(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) =>
        factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false,
            });

    internal static string AuthCookie(HttpResponseMessage response)
    {
        SetCookieHeaderValue cookie = response.Headers
            .GetValues(Microsoft.Net.Http.Headers.HeaderNames.SetCookie)
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(value => value.Name.Value == "pu.auth");
        return $"{cookie.Name.Value}={cookie.Value.Value}";
    }

    internal static IReadOnlySet<string> HeaderNames(HttpResponseMessage response) =>
        response.Headers.Select(header => header.Key)
            .Concat(response.Content.Headers.Select(header => header.Key))
            .Where(name => !name.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
