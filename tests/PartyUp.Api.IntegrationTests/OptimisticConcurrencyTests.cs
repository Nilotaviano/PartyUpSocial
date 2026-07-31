using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class OptimisticConcurrencyTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Save_StaleUserVersionRaisesAConcurrencyConflict()
    {
        UserId userId = await _factory.SeedUserAsync();
        await using AsyncServiceScope firstScope = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope staleScope = _factory.Services.CreateAsyncScope();
        AppDbContext firstContext = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        AppDbContext staleContext = staleScope.ServiceProvider.GetRequiredService<AppDbContext>();
        User first = await firstContext.DomainUsers.SingleAsync(user => user.Id == userId);
        User stale = await staleContext.DomainUsers.SingleAsync(user => user.Id == userId);

        first.RequestDeletion(_factory.Clock);
        await firstContext.SaveChangesAsync();
        stale.Ban(_factory.Clock);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => staleContext.SaveChangesAsync());

        await using AsyncServiceScope verificationScope = _factory.Services.CreateAsyncScope();
        AppDbContext verification = verificationScope.ServiceProvider
            .GetRequiredService<AppDbContext>();
        User persisted = await verification.DomainUsers
            .AsNoTracking()
            .SingleAsync(user => user.Id == userId);
        Assert.Equal(UserLifecycle.PendingDeletion, persisted.Lifecycle);
        Assert.False(persisted.IsBanned);
    }
}
