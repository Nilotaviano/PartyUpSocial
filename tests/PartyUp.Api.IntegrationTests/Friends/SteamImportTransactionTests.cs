using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class SteamImportTransactionTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();
    private readonly MutableSteamFriendListClient _steam = new();
    private WebApplicationFactory<Program>? _configured;

    public async Task InitializeAsync()
    {
        await _factory.StartAsync();
        _configured = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamFriendListClient>();
                services.AddSingleton<ISteamFriendListClient>(_steam);
                services.RemoveAll<INotificationRepository>();
                services.AddScoped<INotificationRepository>(provider =>
                    new FailingSteamImportNotificationRepository(
                        new NotificationRepository(
                            provider.GetRequiredService<AppDbContext>())));
            }));
    }

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose()
    {
        _configured?.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task NotificationWriteFailure_RollsBackAllRowsAndReturnsNoSuccess_D105()
    {
        FriendEndpointUser importer = await SteamImportIntegrationData.SeedLinkedAsync(
            _factory,
            "transaction-importer");
        _ = await SteamImportIntegrationData.SeedLinkedAsync(_factory, "transaction-first");
        _ = await SteamImportIntegrationData.SeedLinkedAsync(_factory, "transaction-second");
        _steam.Result = new SteamFriendListSuccess(
        [
            new SteamFriend("transaction-first", null, null),
            new SteamFriend("transaction-second", null, null),
        ]);

        await using AsyncServiceScope scope = _configured!.Services.CreateAsyncScope();
        ImportSteamFriendsUseCase useCase =
            scope.ServiceProvider.GetRequiredService<ImportSteamFriendsUseCase>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.ExecuteAsync(importer.Id, default));

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            0,
            await context.Notifications.CountAsync(notification =>
                notification.Type == NotificationType.SteamImportMatch));
    }
}

internal sealed class FailingSteamImportNotificationRepository(
    INotificationRepository inner) : INotificationRepository
{
    private int _steamWrites;

    public void Add(Notification notification) => inner.Add(notification);

    public Task<Notification?> FindAsync(NotificationId id, CancellationToken cancellationToken) =>
        inner.FindAsync(id, cancellationToken);

    public async Task AddSteamMatchIfAbsentAsync(
        Notification notification,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _steamWrites) == 2)
        {
            throw new InvalidOperationException("Injected Steam notification failure.");
        }

        await inner.AddSteamMatchIfAbsentAsync(notification, cancellationToken);
    }

    public Task DeleteForRecipientAsync(
        UserId recipientUserId,
        CancellationToken cancellationToken) =>
        inner.DeleteForRecipientAsync(recipientUserId, cancellationToken);
}

internal static class SteamImportIntegrationData
{
    internal static async Task<FriendEndpointUser> SeedLinkedAsync(
        PartyUpApiFactory factory,
        string steamId64)
    {
        FriendEndpointUser user = await FriendEndpointTest.SeedAsync(factory);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.SteamIdentities.Add(new SteamIdentity(
            user.Id,
            steamId64,
            "Steam User",
            null,
            factory.Clock.GetCurrentInstant()));
        await context.SaveChangesAsync();
        return user;
    }
}
