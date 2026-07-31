using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class LoginEnumerationTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Login_AgainstALockedOutAccount_IsIndistinguishableFromAnUnknownAddress_D067()
    {
        const string lockedAddress = "locked-login@example.test";
        await _factory.SeedUserAsync(
            email: lockedAddress,
            password: "correct-password");
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        for (int index = 0; index < 5; index++)
        {
            HttpResponseMessage failure = await LoginAsync(client, lockedAddress);
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        HttpResponseMessage locked = await LoginAsync(client, lockedAddress);
        HttpResponseMessage unknown = await LoginAsync(client, "unknown-login@example.test");
        AuthProblem lockedBody = (await locked.Content.ReadFromJsonAsync<AuthProblem>())!;
        AuthProblem unknownBody = (await unknown.Content.ReadFromJsonAsync<AuthProblem>())!;

        Assert.Equal(locked.StatusCode, unknown.StatusCode);
        Assert.Equal("invalid_credentials", lockedBody.Code);
        Assert.Equal(lockedBody.Code, unknownBody.Code);
        Assert.True(Slice5HttpTest.HeaderNames(locked)
            .SetEquals(Slice5HttpTest.HeaderNames(unknown)));
    }

    [Fact]
    public async Task Login_AllFourNegativeOutcomesAreIndistinguishable_D067()
    {
        const string wrongPasswordAddress = "wrong-password@example.test";
        const string passwordlessAddress = "passwordless@example.test";
        const string lockedAddress = "locked-four-way@example.test";
        await _factory.SeedUserAsync(
            email: wrongPasswordAddress,
            password: "correct-password");
        await _factory.SeedUserAsync(email: passwordlessAddress);
        var lockedUserId = await _factory.SeedUserAsync(
            email: lockedAddress,
            password: "correct-password");
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            AuthUser locked = (await users.FindByIdAsync(
                lockedUserId.Value.ToString("D")))!;
            IdentityResult result = await users.SetLockoutEndDateAsync(
                locked,
                DateTimeOffset.UtcNow.AddMinutes(15));
            Assert.True(result.Succeeded);
        }

        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string[] addresses =
        [
            "unknown-four-way@example.test",
            wrongPasswordAddress,
            passwordlessAddress,
            lockedAddress,
        ];
        var outcomes = new List<(HttpResponseMessage Response, byte[] Body)>();
        foreach (string address in addresses)
        {
            int before = _factory.PasswordHasher.Verifications;
            HttpResponseMessage response = await LoginAsync(client, address);
            int after = _factory.PasswordHasher.Verifications;
            outcomes.Add((response, await response.Content.ReadAsByteArrayAsync()));
            Assert.Equal(1, after - before);
        }

        (HttpResponseMessage baselineResponse, byte[] baselineBody) = outcomes[0];
        Assert.Equal(HttpStatusCode.Unauthorized, baselineResponse.StatusCode);
        Assert.False(baselineResponse.Headers.Contains(HeaderNames.SetCookie));
        foreach ((HttpResponseMessage response, byte[] body) in outcomes.Skip(1))
        {
            Assert.Equal(baselineResponse.StatusCode, response.StatusCode);
            Assert.Equal(baselineBody, body);
            Assert.True(Slice5HttpTest.HeaderNames(baselineResponse)
                .SetEquals(Slice5HttpTest.HeaderNames(response)));
            Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
        }
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email, password = "wrong-password" }));
}
