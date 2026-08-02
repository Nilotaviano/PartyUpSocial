using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Notifications;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class SteamNotificationConcurrencyTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();
    private readonly BarrierSteamFriendListClient _steam = new();
    private WebApplicationFactory<Program>? _configured;

    public async Task InitializeAsync()
    {
        await _factory.StartAsync();
        _configured = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamFriendListClient>();
                services.AddSingleton<ISteamFriendListClient>(_steam);
            }));
    }

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose()
    {
        _configured?.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task ConcurrentImports_CreateOneMatchNotification_D105()
    {
        FriendEndpointUser importer = await SteamImportIntegrationData.SeedLinkedAsync(
            _factory,
            "concurrent-importer");
        FriendEndpointUser matched = await SteamImportIntegrationData.SeedLinkedAsync(
            _factory,
            "concurrent-match");
        _steam.Result = new SteamFriendListSuccess(
            [new SteamFriend("concurrent-match", null, null)]);

        await using AsyncServiceScope firstScope = _configured!.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = _configured.Services.CreateAsyncScope();
        Task<ImportSteamFriendsResult> first = firstScope.ServiceProvider
            .GetRequiredService<ImportSteamFriendsUseCase>()
            .ExecuteAsync(importer.Id, default);
        Task<ImportSteamFriendsResult> second = secondScope.ServiceProvider
            .GetRequiredService<ImportSteamFriendsUseCase>()
            .ExecuteAsync(importer.Id, default);

        ImportSteamFriendsResult[] results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.IsType<SteamImportReady>(result));
        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Notification notification = await context.Notifications.AsNoTracking()
            .SingleAsync(value => value.Type == NotificationType.SteamImportMatch);
        Assert.Equal(importer.Id, notification.RecipientUserId);
        Assert.Equal(matched.Id, notification.ActorUserId);
    }
}

internal sealed class BarrierSteamFriendListClient : ISteamFriendListClient
{
    private readonly TaskCompletionSource _bothEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entries;

    internal SteamFriendListResult Result { get; set; } = new SteamFriendListNotConfigured();

    public async Task<SteamFriendListResult> GetAsync(
        string steamId64,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _entries) == 2)
        {
            _bothEntered.TrySetResult();
        }

        await _bothEntered.Task.WaitAsync(cancellationToken);
        return Result;
    }
}
