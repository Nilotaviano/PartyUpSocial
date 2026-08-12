using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Avatars;

/// <summary>
/// AVATARS_DESIGN.md §5's wire-surface obligations: a Google-sourced avatar survives sign-in and is
/// served by the proxy (D-190); no response body anywhere emits an upstream avatar URL except the
/// named <c>SteamUnmatchedFriendResponse</c> carve-out (D-191); and the blocked-users list carries no
/// avatar property at all (D-192).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvatarWireSurfaceTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// AVATARS_DESIGN.md §4/§5: a GoogleAccount-kind <see cref="AvatarSource"/> -- captured the way
    /// <c>CompleteExternalSignInUseCase</c> captures one at the Google callback -- survives being
    /// picked as the user's avatar and round-trips through <c>GET /api/v1/me</c> and the <c>/a/{token}</c>
    /// proxy with the captured bytes.
    /// </summary>
    [Fact]
    public async Task GoogleSourcedAvatarSurvivesSignInAndServesViaProxy_D190()
    {
        UserId userId = await _factory.SeedUserAsync(email: $"google-avatar-{Guid.NewGuid():N}@example.test");
        byte[] bytes = [9, 8, 7, 6, 5];
        string token;

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User user = await context.DomainUsers.SingleAsync(u => u.Id == userId);
            AvatarSource source = user.CaptureAvatarSource(
                AvatarSourceKind.GoogleAccount,
                bytes,
                "image/webp",
                _factory.Clock);
            token = source.Token;
            user.ChangeAvatar(Avatar.FromSource(source.Id));
            await context.SaveChangesAsync();
        }

        using HttpClient authClient = Slice5HttpTest.CreateClient(_factory);
        HttpResponseMessage signIn = await AntiforgeryTestClient.PostAsync(
            authClient,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        string authCookie = Slice5HttpTest.AuthCookie(signIn);

        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        meRequest.Headers.Add(HeaderNames.Cookie, authCookie);
        using HttpResponseMessage meResponse = await authClient.SendAsync(meRequest);
        meResponse.EnsureSuccessStatusCode();
        using JsonDocument me = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync());
        JsonElement avatar = me.RootElement.GetProperty("avatar");
        Assert.Equal("external", avatar.GetProperty("kind").GetString());
        string imgUrl = avatar.GetProperty("imgUrl").GetString()!;
        Assert.Equal($"/a/{token}", imgUrl);

        using HttpClient proxyClient = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage picture = await proxyClient.GetAsync(imgUrl);
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal(bytes, await picture.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// AVATARS_DESIGN.md §5/D-191: every schema property that looks like an avatar picture field is
    /// either the proxy's own <c>imgUrl</c> (a first-party, same-origin path) or the one named
    /// carve-out. Nothing else names an <c>avatarUrl</c>-shaped field, which is how an upstream
    /// steamstatic/googleusercontent URL would have to reach a client if D-191 regressed.
    /// </summary>
    [Fact]
    public async Task NoResponseBodyEmitsAnUpstreamAvatarUrlExceptTheNamedCarveOut_D191()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var offenders = new List<string>();
        foreach (JsonProperty schema in schemas.EnumerateObject())
        {
            if (schema.Name == "SteamUnmatchedFriendResponse")
            {
                // The one carve-out D-191 names explicitly (FriendEndpoints.cs:709): these people
                // have no User row and therefore no avatar source to proxy.
                continue;
            }

            if (!schema.Value.TryGetProperty("properties", out JsonElement properties))
            {
                continue;
            }

            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (property.Name.Contains("avatarUrl", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{schema.Name}.{property.Name}");
                }
            }
        }

        Assert.Empty(offenders);

        // The carve-out itself still exists and still names its field exactly this way -- a
        // rename there should fail this test too, not silently widen the exception.
        JsonElement unmatched = schemas.GetProperty("SteamUnmatchedFriendResponse");
        Assert.True(unmatched.GetProperty("properties").TryGetProperty("avatarUrl", out _));
    }

    /// <summary>AVATARS_DESIGN.md §1/D-192: amends D-188 -- the blocked-users list carries no avatar at all.</summary>
    [Fact]
    public async Task BlockedUserResponseCarriesNoAvatarProperty_D192()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("BlockedUserResponse", out JsonElement blockedUser), "Missing BlockedUserResponse schema");
        JsonElement properties = blockedUser.GetProperty("properties");
        Assert.False(properties.TryGetProperty("avatar", out _));
        Assert.Equal(
            ["blockId", "createdAt", "username"],
            properties.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }
}
