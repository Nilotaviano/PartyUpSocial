using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class UnlinkAuthMethodConcurrencyTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Unlink_TwoOverlappingTransactions_LeaveOneMethod_D137()
    {
        UserId userId = await _factory.SeedUserAsync(
            email: null,
            password: "Concurrent-Unlink-Password-42!");
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAccountGateway>()
                .AddLoginAsync(
                    userId,
                    ExternalProviderPolicy.Google,
                    "concurrent-google-key",
                    default);
        }
        var barrier = new FindForUpdateBarrier();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserRepository>();
                services.AddScoped<IUserRepository>(provider =>
                    new BarrierUserRepository(
                        provider.GetRequiredService<UserRepository>(),
                        barrier));
            }));

        Task<UnlinkAuthMethodResult> removePassword = ExecuteAsync(
            factory.Services,
            new UnlinkAuthMethodCommand(userId, "password", true));
        Task<UnlinkAuthMethodResult> removeGoogle = ExecuteAsync(
            factory.Services,
            new UnlinkAuthMethodCommand(userId, "google", true));
        UnlinkAuthMethodResult[] results =
            await Task.WhenAll(removePassword, removeGoogle);

        Assert.Equal(2, barrier.Arrivals);
        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => result.Code == "last_auth_method");
        await using AsyncServiceScope verificationScope =
            factory.Services.CreateAsyncScope();
        IUserAccountGateway accounts = verificationScope.ServiceProvider
            .GetRequiredService<IUserAccountGateway>();
        int remaining = await accounts.HasPasswordAsync(userId, default) ? 1 : 0;
        remaining += (await accounts.ListLoginProvidersAsync(userId, default)).Count;
        Assert.Equal(1, remaining);
    }

    private static async Task<UnlinkAuthMethodResult> ExecuteAsync(
        IServiceProvider services,
        UnlinkAuthMethodCommand command)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UnlinkAuthMethodUseCase>()
            .ExecuteAsync(command, default);
    }

    private sealed class FindForUpdateBarrier
    {
        private readonly TaskCompletionSource _bothArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public int Arrivals => Volatile.Read(ref _arrivals);

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _bothArrived.TrySetResult();
            }

            await _bothArrived.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);
        }
    }

    private sealed class BarrierUserRepository(
        UserRepository inner,
        FindForUpdateBarrier barrier) : IUserRepository
    {
        public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken) =>
            inner.FindAsync(id, cancellationToken);

        public async Task<User?> FindForUpdateAsync(
            UserId id,
            CancellationToken cancellationToken)
        {
            await barrier.ArriveAsync(cancellationToken);
            return await inner.FindForUpdateAsync(id, cancellationToken);
        }

        public Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
            Instant now,
            int max,
            CancellationToken cancellationToken) =>
            inner.FindDueForDeletionAsync(now, max, cancellationToken);

        public void Add(User user) => inner.Add(user);
    }
}
