using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class UsernameClaimConcurrencyTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Complete_TwoConcurrentClaimsHaveOneWinner_D128()
    {
        UserId firstUser = await _factory.SeedUserAsync(
            completeProfile: false,
            email: "first-claim@example.test");
        UserId secondUser = await _factory.SeedUserAsync(
            completeProfile: false,
            email: "second-claim@example.test");
        var barrier = new AsyncBarrier(2);
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUsernameRegistryRepository>();
                services.AddScoped<UsernameRegistryRepository>();
                services.AddScoped<IUsernameRegistryRepository>(provider =>
                    new BarrierUsernameRepository(
                        provider.GetRequiredService<UsernameRegistryRepository>(),
                        barrier,
                        "caseclaim"));
            }));
        using HttpClient firstClient = Slice5HttpTest.CreateClient(factory);
        using HttpClient secondClient = Slice5HttpTest.CreateClient(factory);
        string firstCookie = await SignInAsync(firstClient, firstUser);
        string secondCookie = await SignInAsync(secondClient, secondUser);

        Task<HttpResponseMessage>[] requests =
        [
            CompleteAsync(firstClient, firstCookie, "CaseClaim"),
            CompleteAsync(secondClient, secondCookie, "caseclaim"),
        ];
        HttpResponseMessage[] responses = await Task.WhenAll(requests);

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        HttpResponseMessage conflict = Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Conflict);
        using JsonDocument conflictBody = JsonDocument.Parse(
            await conflict.Content.ReadAsStringAsync());
        Assert.Equal("duplicate", conflictBody.RootElement.GetProperty("code").GetString());

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UsernameRegistryEntry registry = await context.UsernameRegistry
            .AsNoTracking()
            .SingleAsync(entry => entry.NameCanonical == "caseclaim");
        User[] users = await context.DomainUsers
            .AsNoTracking()
            .Where(user => user.Id == firstUser || user.Id == secondUser)
            .ToArrayAsync();
        User winner = Assert.Single(users, user => user.Lifecycle == UserLifecycle.Active);
        User loser = Assert.Single(users, user => user.Lifecycle == UserLifecycle.Onboarding);
        Assert.Equal(winner.Id, registry.OwnerId);
        Assert.Equal("caseclaim", UsernameRegistry.Canonicalize(winner.Username!.Value));
        Assert.Null(loser.Username);
        Assert.False(loser.IsProfileComplete);
        Assert.Equal(2, await context.AuthUsers.CountAsync(user =>
            user.Id == firstUser.Value || user.Id == secondUser.Value));
    }

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private static Task<HttpResponseMessage> CompleteAsync(
        HttpClient client,
        string cookie,
        string username) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/onboarding/complete",
            JsonContent.Create(new
            {
                username,
                displayName = "Claim Player",
                avatar = new { kind = "emoji", emoji = "🙂" },
                timeZone = "America/Fortaleza",
                locale = "en",
            }),
            cookie);

    private sealed class BarrierUsernameRepository(
        UsernameRegistryRepository inner,
        AsyncBarrier barrier,
        string targetCanonical) : IUsernameRegistryRepository
    {
        public async Task<UsernameRegistryEntry?> FindAsync(
            string canonical,
            CancellationToken cancellationToken)
        {
            UsernameRegistryEntry? result = await inner.FindAsync(canonical, cancellationToken);
            if (string.Equals(canonical, targetCanonical, StringComparison.Ordinal))
            {
                Assert.Null(result);
                await barrier.SignalAndWaitAsync();
            }

            return result;
        }

        public Task<UsernameRegistryEntry?> FindActiveForOwnerAsync(
            UserId owner,
            CancellationToken cancellationToken) =>
            inner.FindActiveForOwnerAsync(owner, cancellationToken);

        public void Add(UsernameRegistryEntry entry) => inner.Add(entry);
    }

    private sealed class AsyncBarrier(int participants)
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        internal Task SignalAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) == participants)
            {
                _release.TrySetResult();
            }

            return _release.Task;
        }
    }
}
