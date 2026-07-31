using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class LoginCancelsDeletionTests : IAsyncLifetime, IDisposable
{
    private const string Password = "correct-password";
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Login_DuringGrace_CancelsAndRestores_D158()
    {
        const string email = "cancel-deletion@example.test";
        UserId userId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: email,
            password: Password);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage response = await LoginAsync(client, email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SessionResponse session = (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
        Assert.Equal("active", session.State);
        User user = await ReadUserAsync(userId);
        Assert.True(user.IsActive);
        Assert.Null(user.GraceEndsAt);
    }

    [Fact]
    public async Task Login_DuringGrace_ForAnOnboardingAccount_ReturnsToOnboarding_D158()
    {
        const string email = "onboarding-cancel@example.test";
        UserId userId = await _factory.SeedUserAsync(
            completeProfile: false,
            pendingDeletion: true,
            email: email,
            password: Password);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage response = await LoginAsync(client, email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SessionResponse session = (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
        Assert.Equal("onboarding", session.State);
        Assert.True((await ReadUserAsync(userId)).IsOnboarding);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email, password = Password }));

    private async Task<User> ReadUserAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .FindAsync(userId, default))!;
    }
}
