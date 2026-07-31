using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class OnboardingDeletionTests : IAsyncLifetime, IDisposable
{
    private const string Password = "correct-password";
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RequestDeletion_WhileOnboarding_StartsTheGrace_D072()
    {
        UserId userId = await _factory.SeedUserAsync(
            completeProfile: false,
            password: Password);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, userId);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/me/deletion",
            JsonContent.Create(new { currentPassword = Password }),
            cookie);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .FindAsync(userId, default))!.IsPendingDeletion);
    }

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        return response.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }
}
