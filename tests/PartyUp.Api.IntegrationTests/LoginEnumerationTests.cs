using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Domain.Kernel;
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

    [Fact]
    public async Task Login_ConcurrentFailuresReachLockoutWithoutDistinctErrors_D067()
    {
        const string email = "concurrent-lockout@example.test";
        UserId userId = await _factory.SeedUserAsync(
            email: email,
            password: "correct-password");
        var barrierHasher = new BarrierPasswordHasher(5);
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Network:TrustedProxies:0"] = "10.0.0.2",
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(
                    IPAddress.Parse("10.0.0.2"))));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPasswordHasher<AuthUser>>();
                services.AddSingleton<IPasswordHasher<AuthUser>>(barrierHasher);
            });
        });
        using HttpClient client = Slice5HttpTest.CreateClient(factory);

        Task<HttpResponseMessage>[] attempts = Enumerable.Range(1, 5)
            .Select(index => AntiforgeryTestClient.PostAsync(
                client,
                "/api/v1/auth/login",
                JsonContent.Create(new { email, password = "wrong-password" }),
                forwardedFor: $"192.0.2.{index}"))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(attempts);

        Assert.All(responses, response =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
        });
        AuthProblem[] bodies = await Task.WhenAll(responses.Select(async response =>
            (await response.Content.ReadFromJsonAsync<AuthProblem>())!));
        Assert.All(bodies, body => Assert.Equal("invalid_credentials", body.Code));

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            AuthUser account = (await users.FindByIdAsync(userId.Value.ToString("D")))!;
            Assert.Equal(0, account.AccessFailedCount);
            Assert.True(await users.IsLockedOutAsync(account));
            Assert.NotNull(account.LockoutEnd);
        }

        HttpResponseMessage locked = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email, password = "correct-password" }),
            forwardedFor: "192.0.2.100");
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal(
            "invalid_credentials",
            (await locked.Content.ReadFromJsonAsync<AuthProblem>())!.Code);
        Assert.False(locked.Headers.Contains(HeaderNames.SetCookie));
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email, password = "wrong-password" }));

    private sealed class BarrierPasswordHasher(int participants) : IPasswordHasher<AuthUser>
    {
        private readonly PasswordHasher<AuthUser> _inner = new();
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public string HashPassword(AuthUser user, string password) =>
            _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(
            AuthUser user,
            string hashedPassword,
            string providedPassword)
        {
            if (Interlocked.Increment(ref _arrived) == participants)
            {
                _release.TrySetResult();
            }

            _release.Task.GetAwaiter().GetResult();
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

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
