using System.Net;
using System.Net.Http.Json;
using PartyUp.Api.Endpoints;

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

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email, password = "wrong-password" }));
}
