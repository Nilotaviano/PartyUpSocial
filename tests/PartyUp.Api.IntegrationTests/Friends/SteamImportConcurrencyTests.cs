using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Application.Friends;
using PartyUp.Domain.Notifications;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class SteamImportConcurrencyTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();
    private readonly RoutingSteamFriendListClient _steam = new();
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
    public async Task BlockRacingImport_ProducesOneOfTwoLinearizedOrderings_D065()
    {
        FriendEndpointUser blockFirstImporter =
            await SteamImportIntegrationData.SeedLinkedAsync(_factory, "block-first-importer");
        FriendEndpointUser blockFirstCandidate =
            await SteamImportIntegrationData.SeedLinkedAsync(_factory, "block-first-match");
        _steam.Results["block-first-importer"] = new SteamFriendListSuccess(
            [new SteamFriend("block-first-match", null, null)]);

        await using (AsyncServiceScope scope = _configured!.Services.CreateAsyncScope())
        {
            BlockGraphCommandResult block = await scope.ServiceProvider
                .GetRequiredService<BlockUserUseCase>()
                .ExecuteAsync(
                    blockFirstImporter.Id,
                    blockFirstCandidate.Username,
                    default);
            Assert.True(block.Succeeded);
        }

        ImportSteamFriendsResult blockFirst = await ImportAsync(blockFirstImporter.Id);
        SteamImportZeroMatches blockFirstResult =
            Assert.IsType<SteamImportZeroMatches>(blockFirst);
        Assert.Empty(blockFirstResult.Matches);
        Assert.Single(blockFirstResult.Unmatched);

        FriendEndpointUser importFirstImporter =
            await SteamImportIntegrationData.SeedLinkedAsync(_factory, "import-first-user");
        FriendEndpointUser importFirstCandidate =
            await SteamImportIntegrationData.SeedLinkedAsync(_factory, "import-first-match");
        _steam.Results["import-first-user"] = new SteamFriendListSuccess(
            [new SteamFriend("import-first-match", null, null)]);

        SteamImportReady importFirst = Assert.IsType<SteamImportReady>(
            await ImportAsync(importFirstImporter.Id));
        Assert.Equal(importFirstCandidate.Username, Assert.Single(importFirst.Matches).Username);
        await using (AsyncServiceScope scope = _configured.Services.CreateAsyncScope())
        {
            BlockGraphCommandResult block = await scope.ServiceProvider
                .GetRequiredService<BlockUserUseCase>()
                .ExecuteAsync(
                    importFirstImporter.Id,
                    importFirstCandidate.Username,
                    default);
            Assert.True(block.Succeeded);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            0,
            await context.Notifications.CountAsync(notification =>
                notification.RecipientUserId == blockFirstImporter.Id &&
                notification.Type == NotificationType.SteamImportMatch));
        Assert.Equal(
            1,
            await context.Notifications.CountAsync(notification =>
                notification.RecipientUserId == importFirstImporter.Id &&
                notification.ActorUserId == importFirstCandidate.Id &&
                notification.Type == NotificationType.SteamImportMatch));
    }

    [Fact]
    public async Task UnlinkOrRelinkRacingImport_RevalidatesImporterAndCandidateOwnershipInBothOrderings_D049()
    {
        FriendEndpointUser importer =
            await SteamImportIntegrationData.SeedLinkedAsync(_factory, "old-importer-steam");
        FriendEndpointUser oldCandidate =
            await SteamImportIntegrationData.SeedLinkedAsync(_factory, "old-candidate-steam");
        FriendEndpointUser newCandidate =
            await SteamImportIntegrationData.SeedLinkedAsync(_factory, "new-candidate-steam");
        _steam.Results["old-importer-steam"] = new SteamFriendListSuccess(
            [new SteamFriend("old-candidate-steam", null, null)]);
        SteamImportReady importFirst = Assert.IsType<SteamImportReady>(
            await ImportAsync(importer.Id));
        Assert.Equal(oldCandidate.Username, Assert.Single(importFirst.Matches).Username);

        await UpdateSteamIdentityAsync(importer.Id, "new-importer-steam");
        _steam.Results["new-importer-steam"] = new SteamFriendListSuccess(
            [new SteamFriend("new-candidate-steam", null, null)]);
        var importerRace = new SteamOwnershipRaceState
        {
            ForcedInitialImporterSteamId = "old-importer-steam",
        };
        await using (WebApplicationFactory<Program> raceFactory = OwnershipRaceFactory(importerRace))
        {
            SteamImportReady relinkFirst = Assert.IsType<SteamImportReady>(
                await ImportAsync(importer.Id, raceFactory));
            Assert.Equal(newCandidate.Username, Assert.Single(relinkFirst.Matches).Username);
        }
        Assert.Equal(
            ["old-importer-steam", "old-importer-steam", "new-importer-steam"],
            _steam.RequestedSteamIds.TakeLast(3).ToArray());

        FriendEndpointUser staleOwner = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser currentOwner =
            await SteamImportIntegrationData.SeedLinkedAsync(_factory, "changed-owner-steam");
        FriendEndpointUser ownershipImporter =
            await SteamImportIntegrationData.SeedLinkedAsync(_factory, "ownership-importer");
        _steam.Results["ownership-importer"] = new SteamFriendListSuccess(
            [new SteamFriend("changed-owner-steam", null, null)]);
        var candidateRace = new SteamOwnershipRaceState
        {
            StaleCandidateSteamId = "changed-owner-steam",
            StaleCandidateUserId = staleOwner.Id,
        };
        await using (WebApplicationFactory<Program> raceFactory = OwnershipRaceFactory(candidateRace))
        {
            SteamImportReady retried = Assert.IsType<SteamImportReady>(
                await ImportAsync(ownershipImporter.Id, raceFactory));
            Assert.Equal(currentOwner.Username, Assert.Single(retried.Matches).Username);
        }
        Assert.True(candidateRace.OwnerProjectionCalls >= 2);

        await RemoveSteamIdentityAsync(ownershipImporter.Id);
        Assert.IsType<SteamImportNotLinked>(await ImportAsync(ownershipImporter.Id));

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            0,
            await context.Notifications.CountAsync(notification =>
                notification.RecipientUserId == ownershipImporter.Id &&
                notification.ActorUserId == staleOwner.Id));
        Assert.Equal(
            1,
            await context.Notifications.CountAsync(notification =>
                notification.RecipientUserId == ownershipImporter.Id &&
                notification.ActorUserId == currentOwner.Id));
    }

    [Fact]
    public async Task SteamLink_WaitsForAffectedUserLockBeforeChangingA030Facts()
    {
        FriendEndpointUser user = await FriendEndpointTest.SeedAsync(_factory);
        var attempt = new UserLockAttemptState();
        await using WebApplicationFactory<Program> lockFactory =
            _configured!.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IUserRepository>();
                    services.AddSingleton(attempt);
                    services.AddScoped<UserRepository>();
                    services.AddScoped<IUserRepository>(provider =>
                        new AttemptingUserRepository(
                            provider.GetRequiredService<UserRepository>(),
                            provider.GetRequiredService<UserLockAttemptState>()));
                }));
        await using AsyncServiceScope holdingScope = _factory.Services.CreateAsyncScope();
        AppDbContext holdingContext =
            holdingScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using IDbContextTransaction transaction =
            await holdingContext.Database.BeginTransactionAsync();
        await holdingContext.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM users WHERE id = {user.Id.Value} FOR UPDATE
            """);

        await using AsyncServiceScope commandScope = lockFactory.Services.CreateAsyncScope();
        Task<ExternalSignInResult> linking = commandScope.ServiceProvider
            .GetRequiredService<CompleteExternalSignInUseCase>()
            .ExecuteAsync(
                new CompleteExternalSignInCommand(
                    "steam",
                    "a030-steam-link",
                    null,
                    false,
                    ExternalStartMode.Link,
                    false,
                    user.Id,
                    user.Id),
                default);
        await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(linking.IsCompleted);

        await transaction.CommitAsync();
        Assert.True((await linking).MethodLinked);
    }

    private async Task<ImportSteamFriendsResult> ImportAsync(
        PartyUp.Domain.Kernel.UserId importerId,
        WebApplicationFactory<Program>? factory = null)
    {
        await using AsyncServiceScope scope =
            (factory ?? _configured!).Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<ImportSteamFriendsUseCase>()
            .ExecuteAsync(importerId, default);
    }

    private WebApplicationFactory<Program> OwnershipRaceFactory(SteamOwnershipRaceState state) =>
        _configured!.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamImportAccountGateway>();
                services.AddSingleton(state);
                services.AddScoped<UserAccountGateway>();
                services.AddScoped<ISteamImportAccountGateway>(provider =>
                    new RacingSteamImportAccountGateway(
                        provider.GetRequiredService<UserAccountGateway>(),
                        provider.GetRequiredService<SteamOwnershipRaceState>()));
            }));

    private async Task UpdateSteamIdentityAsync(
        PartyUp.Domain.Kernel.UserId userId,
        string steamId64)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SteamIdentity identity = await context.SteamIdentities.SingleAsync(value =>
            value.UserId == userId);
        identity.Update(steamId64, "Relinked", null, _factory.Clock.GetCurrentInstant());
        await context.SaveChangesAsync();
    }

    private async Task RemoveSteamIdentityAsync(PartyUp.Domain.Kernel.UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SteamIdentity identity = await context.SteamIdentities.SingleAsync(value =>
            value.UserId == userId);
        context.SteamIdentities.Remove(identity);
        await context.SaveChangesAsync();
    }
}

internal sealed class RoutingSteamFriendListClient : ISteamFriendListClient
{
    internal Dictionary<string, SteamFriendListResult> Results { get; } =
        new(StringComparer.Ordinal);

    internal List<string> RequestedSteamIds { get; } = [];

    public Task<SteamFriendListResult> GetAsync(
        string steamId64,
        CancellationToken cancellationToken)
    {
        RequestedSteamIds.Add(steamId64);
        return Task.FromResult(Results[steamId64]);
    }
}

internal sealed class SteamOwnershipRaceState
{
    private int _initialImporterReads;
    private int _ownerProjectionCalls;

    internal string? ForcedInitialImporterSteamId { get; init; }

    internal string? StaleCandidateSteamId { get; init; }

    internal PartyUp.Domain.Kernel.UserId? StaleCandidateUserId { get; init; }

    internal int OwnerProjectionCalls => Volatile.Read(ref _ownerProjectionCalls);

    internal bool UseForcedImporter() =>
        ForcedInitialImporterSteamId is not null &&
        Interlocked.Increment(ref _initialImporterReads) == 1;

    internal bool UseStaleOwner() =>
        StaleCandidateSteamId is not null &&
        Interlocked.Increment(ref _ownerProjectionCalls) == 1;

    internal void RecordOwnerProjection() => Interlocked.Increment(ref _ownerProjectionCalls);
}

internal sealed class RacingSteamImportAccountGateway(
    ISteamImportAccountGateway inner,
    SteamOwnershipRaceState state) : ISteamImportAccountGateway
{
    public Task<string?> FindSteamIdAsync(
        PartyUp.Domain.Kernel.UserId id,
        CancellationToken cancellationToken) =>
        state.UseForcedImporter()
            ? Task.FromResult<string?>(state.ForcedInitialImporterSteamId)
            : inner.FindSteamIdAsync(id, cancellationToken);

    public async Task<IReadOnlyDictionary<string, PartyUp.Domain.Kernel.UserId>>
        FindSteamIdentityOwnersAsync(
            IReadOnlyCollection<string> steamIds,
            CancellationToken cancellationToken)
    {
        if (state.UseStaleOwner())
        {
            return new Dictionary<string, PartyUp.Domain.Kernel.UserId>(StringComparer.Ordinal)
            {
                [state.StaleCandidateSteamId!] = state.StaleCandidateUserId!.Value,
            };
        }

        state.RecordOwnerProjection();
        return await inner.FindSteamIdentityOwnersAsync(steamIds, cancellationToken);
    }

    public Task LockUsersAsync(
        IReadOnlyCollection<PartyUp.Domain.Kernel.UserId> userIds,
        CancellationToken cancellationToken) => inner.LockUsersAsync(userIds, cancellationToken);

    public Task<SteamImportOwnershipSnapshot> ReadSteamImportOwnershipSnapshotAsync(
        PartyUp.Domain.Kernel.UserId importerId,
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken) =>
        inner.ReadSteamImportOwnershipSnapshotAsync(
            importerId,
            steamIds,
            cancellationToken);
}

internal sealed class UserLockAttemptState
{
    internal TaskCompletionSource Attempted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class AttemptingUserRepository(
    IUserRepository inner,
    UserLockAttemptState state) : IUserRepository
{
    public Task<PartyUp.Domain.Users.User?> FindAsync(
        PartyUp.Domain.Kernel.UserId id,
        CancellationToken cancellationToken) => inner.FindAsync(id, cancellationToken);

    public Task<PartyUp.Domain.Users.User?> FindForUpdateAsync(
        PartyUp.Domain.Kernel.UserId id,
        CancellationToken cancellationToken)
    {
        state.Attempted.TrySetResult();
        return inner.FindForUpdateAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<PartyUp.Domain.Kernel.UserId>> FindDueForDeletionAsync(
        NodaTime.Instant now,
        int max,
        CancellationToken cancellationToken) =>
        inner.FindDueForDeletionAsync(now, max, cancellationToken);

    public void Add(PartyUp.Domain.Users.User user) => inner.Add(user);
}
