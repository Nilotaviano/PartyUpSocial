using System.Net.Http.Json;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;

namespace PartyUp.Api.IntegrationTests;

internal static class AntiforgeryTestClient
{
    internal static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string requestUri,
        HttpContent? content,
        string? authenticationCookie = null,
        string? forwardedFor = null)
    {
        AntiforgeryPair pair = await GetAsync(client, authenticationCookie);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = content,
        };
        request.Headers.Add("X-CSRF-TOKEN", pair.Token);
        request.Headers.Add(
            HeaderNames.Cookie,
            authenticationCookie is null
                ? pair.Cookie
                : $"{authenticationCookie}; {pair.Cookie}");
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await client.SendAsync(request);
    }

    internal static async Task<AntiforgeryPair> GetAsync(
        HttpClient client,
        string? authenticationCookie = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/auth/antiforgery");
        if (authenticationCookie is not null)
        {
            request.Headers.Add(HeaderNames.Cookie, authenticationCookie);
        }

        HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        AntiforgeryTokenResponse body =
            (await response.Content.ReadFromJsonAsync<AntiforgeryTokenResponse>())!;
        string cookie = response.Headers.GetValues(HeaderNames.SetCookie)
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(value => value.Name.Value == "pu.csrf")
            .ToString()
            .Split(';', 2)[0];
        return new AntiforgeryPair(body.Token, cookie);
    }
}

internal sealed record AntiforgeryPair(string Token, string Cookie);
