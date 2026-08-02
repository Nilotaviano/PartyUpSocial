using Microsoft.AspNetCore.Identity;
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
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class SteamImportConcurrencyTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(10);

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
                services.RemoveAll<ISteamProfileClient>();
                services.AddSingleton<ISteamProfileClient>(new StaticSteamProfileClient());
            }));
    }

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose()
    {
        _configured?.Dispose();
        _factory.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlockRacingImport_ProducesExactLockedOrdering_D065(
        bool blockLocksFirst)
    {
        FriendEndpointUser importer = await SeedA030LinkedAsync("block-race-importer");
        FriendEndpointUser candidate = await SeedA030LinkedAsync("block-race-candidate");
        _steam.Results["block-race-importer"] = new SteamFriendListSuccess(
            [new SteamFriend("block-race-candidate", "Candidate", null)]);
        var holdingGate = new PairLockGate();
        var attemptGate = new PairLockAttemptGate();

        await using WebApplicationFactory<Program> raceFactory =
            BlockRaceFactory(blockLocksFirst, holdingGate, attemptGate);
        Task<BlockGraphCommandResult> block;
        Task<ImportSteamFriendsResult> import;
        if (blockLocksFirst)
        {
            block = BlockAsync(importer, candidate, raceFactory);
            await holdingGate.Held.Task.WaitAsync(RaceTimeout);
            import = ImportAsync(importer.Id, raceFactory);
            await AssertBlockedAttemptAsync(import, attemptGate, holdingGate);
        }
        else
        {
            import = ImportAsync(importer.Id, raceFactory);
            await holdingGate.Held.Task.WaitAsync(RaceTimeout);
            block = BlockAsync(importer, candidate, raceFactory);
            await AssertBlockedAttemptAsync(block, attemptGate, holdingGate);
        }

        BlockGraphCommandResult blockResult = await block;
        Assert.True(blockResult.Succeeded);
        Assert.True(blockResult.Created);
        ImportSteamFriendsResult importResult = await import;
        string invitationUrl = $"http://localhost:5173/add/{importer.FriendCode}";
        if (blockLocksFirst)
        {
            SteamImportZeroMatches hidden = Assert.IsType<SteamImportZeroMatches>(importResult);
            Assert.Empty(hidden.Matches);
            SteamUnmatchedFriend unmatched = Assert.Single(hidden.Unmatched);
            Assert.Equal("block-race-candidate", unmatched.SteamId64);
            Assert.Equal(invitationUrl, unmatched.InvitationUrl);
        }
        else
        {
            SteamImportReady matched = Assert.IsType<SteamImportReady>(importResult);
            Assert.Equal(candidate.Username, Assert.Single(matched.Matches).Username);
            Assert.Empty(matched.Unmatched);
            Assert.Equal(invitationUrl, matched.InvitationUrl);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.Blocks.CountAsync());
        Assert.Equal(0, await context.Friendships.CountAsync());
        Assert.Equal(
            blockLocksFirst ? 0 : 1,
            await SteamMatchCountAsync(context, importer.Id, candidate.Id));
        Assert.Null(await new UserGraphResolver(context).ResolveProfileByUsernameAsync(
            importer.Id,
            candidate.Username.ToUpperInvariant(),
            default));
    }

    [Theory]
    [InlineData("unlink", true)]
    [InlineData("unlink", false)]
    [InlineData("relink", true)]
    [InlineData("relink", false)]
    public async Task ImporterA030MutationRacingImport_UsesOnlyItsLockedOwnership_D049(
        string mutation,
        bool mutationLinearizesFirst)
    {
        FriendEndpointUser importer = await SeedA030LinkedAsync("imp-old");
        FriendEndpointUser oldCandidate = await SeedA030LinkedAsync("cand-old");
        FriendEndpointUser newCandidate = await SeedA030LinkedAsync("cand-new");
        _steam.Results["imp-old"] = new SteamFriendListSuccess(
            [new SteamFriend("cand-old", null, null)]);
        _steam.Results["imp-new"] = new SteamFriendListSuccess(
            [new SteamFriend("cand-new", null, null)]);
        int requestBaseline = _steam.RequestedSteamIds.Count;

        ImportSteamFriendsResult result;
        if (mutationLinearizesFirst)
        {
            var projectionGate = new OwnershipProjectionGate();
            await using WebApplicationFactory<Program> raceFactory =
                ProjectionRaceFactory(projectionGate);
            Task<ImportSteamFriendsResult> import = ImportAsync(importer.Id, raceFactory);
            IReadOnlyDictionary<string, UserId> projected =
                await projectionGate.FirstProjection.Task.WaitAsync(RaceTimeout);
            Assert.Equal(oldCandidate.Id, projected["cand-old"]);

            if (mutation == "unlink")
            {
                Assert.True((await UnlinkSteamAsync(importer.Id, raceFactory)).Succeeded);
                projectionGate.ReleaseFirst.TrySetResult();
                result = await import;
            }
            else
            {
                await RelinkSteamAsync(importer.Id, "imp-new", raceFactory);
                RetryWriteSnapshot beforeRetry = await RetrySnapshotAsync(
                    importer.Id,
                    oldCandidate.Id,
                    newCandidate.Id);
                projectionGate.ReleaseFirst.TrySetResult();
                try
                {
                    await projectionGate.RetryProjection.Task.WaitAsync(RaceTimeout);
                    Assert.Equal(beforeRetry, await RetrySnapshotAsync(
                        importer.Id,
                        oldCandidate.Id,
                        newCandidate.Id));
                }
                finally
                {
                    projectionGate.ReleaseRetry.TrySetResult();
                }

                result = await import;
                Assert.Equal(2, projectionGate.ProjectionCalls);
            }
        }
        else
        {
            var holdingGate = new PairLockGate();
            var mutationAttempt = new UserLockAttemptState();
            await using WebApplicationFactory<Program> raceFactory =
                ImportFirstA030RaceFactory(holdingGate, mutationAttempt);
            Task<ImportSteamFriendsResult> import = ImportAsync(importer.Id, raceFactory);
            await holdingGate.Held.Task.WaitAsync(RaceTimeout);
            Task mutationTask = mutation == "unlink"
                ? UnlinkSteamSuccessfullyAsync(importer.Id, raceFactory)
                : RelinkSteamAsync(importer.Id, "imp-new", raceFactory);
            try
            {
                await mutationAttempt.Attempted.Task.WaitAsync(RaceTimeout);
                Assert.False(mutationTask.IsCompleted);
            }
            finally
            {
                holdingGate.Release.TrySetResult();
            }

            result = await import;
            await mutationTask;
        }

        if (mutationLinearizesFirst && mutation == "unlink")
        {
            Assert.IsType<SteamImportNotLinked>(result);
        }
        else
        {
            FriendEndpointUser expected = mutationLinearizesFirst
                ? newCandidate
                : oldCandidate;
            SteamImportReady ready = Assert.IsType<SteamImportReady>(result);
            Assert.Equal(expected.Username, Assert.Single(ready.Matches).Username);
        }

        string[] requested = _steam.RequestedSteamIds.Skip(requestBaseline).ToArray();
        Assert.Equal(
            mutationLinearizesFirst && mutation == "relink"
                ? ["imp-old", "imp-new"]
                : ["imp-old"],
            requested);
        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            mutationLinearizesFirst && mutation == "relink" ? 1 : 0,
            await SteamMatchCountAsync(context, importer.Id, newCandidate.Id));
        Assert.Equal(
            mutationLinearizesFirst ? 0 : 1,
            await SteamMatchCountAsync(context, importer.Id, oldCandidate.Id));
        Assert.Equal(
            mutation == "unlink" ? null : "imp-new",
            await CurrentSteamIdAsync(context, importer.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CandidateRelinkRacingImport_RetriesChangedLockSetWithoutWrites_D049(
        bool relinkLinearizesFirst)
    {
        FriendEndpointUser importer = await SeedA030LinkedAsync("owner-race-importer");
        FriendEndpointUser oldOwner = await SeedA030LinkedAsync("owner-race-steam");
        FriendEndpointUser newOwner = await FriendEndpointTest.SeedAsync(_factory);
        _steam.Results["owner-race-importer"] = new SteamFriendListSuccess(
            [new SteamFriend("owner-race-steam", null, null)]);

        ImportSteamFriendsResult result;
        if (relinkLinearizesFirst)
        {
            var projectionGate = new OwnershipProjectionGate();
            await using WebApplicationFactory<Program> raceFactory =
                ProjectionRaceFactory(projectionGate);
            Task<ImportSteamFriendsResult> import = ImportAsync(importer.Id, raceFactory);
            IReadOnlyDictionary<string, UserId> projected =
                await projectionGate.FirstProjection.Task.WaitAsync(RaceTimeout);
            Assert.Equal(oldOwner.Id, projected["owner-race-steam"]);

            Assert.True((await UnlinkSteamAsync(oldOwner.Id, raceFactory)).Succeeded);
            Assert.True((await LinkSteamAsync(
                newOwner.Id,
                "owner-race-steam",
                raceFactory)).MethodLinked);
            RetryWriteSnapshot beforeRetry = await RetrySnapshotAsync(
                importer.Id,
                oldOwner.Id,
                newOwner.Id);
            projectionGate.ReleaseFirst.TrySetResult();
            try
            {
                IReadOnlyDictionary<string, UserId> retryProjection =
                    await projectionGate.RetryProjection.Task.WaitAsync(RaceTimeout);
                Assert.Equal(newOwner.Id, retryProjection["owner-race-steam"]);
                Assert.Equal(beforeRetry, await RetrySnapshotAsync(
                    importer.Id,
                    oldOwner.Id,
                    newOwner.Id));
            }
            finally
            {
                projectionGate.ReleaseRetry.TrySetResult();
            }

            result = await import;
            Assert.Equal(2, projectionGate.ProjectionCalls);
        }
        else
        {
            var holdingGate = new PairLockGate();
            var mutationAttempt = new UserLockAttemptState();
            await using WebApplicationFactory<Program> raceFactory =
                ImportFirstA030RaceFactory(holdingGate, mutationAttempt);
            Task<ImportSteamFriendsResult> import = ImportAsync(importer.Id, raceFactory);
            await holdingGate.Held.Task.WaitAsync(RaceTimeout);
            Task relink = TransferSteamAsync(
                oldOwner.Id,
                newOwner.Id,
                "owner-race-steam",
                raceFactory);
            try
            {
                await mutationAttempt.Attempted.Task.WaitAsync(RaceTimeout);
                Assert.False(relink.IsCompleted);
            }
            finally
            {
                holdingGate.Release.TrySetResult();
            }

            result = await import;
            await relink;
        }

        FriendEndpointUser expected = relinkLinearizesFirst ? newOwner : oldOwner;
        FriendEndpointUser stale = relinkLinearizesFirst ? oldOwner : newOwner;
        SteamImportReady ready = Assert.IsType<SteamImportReady>(result);
        Assert.Equal(expected.Username, Assert.Single(ready.Matches).Username);
        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(newOwner.Id, await CurrentSteamOwnerAsync(context, "owner-race-steam"));
        Assert.Equal(1, await SteamMatchCountAsync(context, importer.Id, expected.Id));
        Assert.Equal(0, await SteamMatchCountAsync(context, importer.Id, stale.Id));
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

        Task<ExternalSignInResult> linking = LinkSteamAsync(
            user.Id,
            "a030-steam-link",
            lockFactory);
        await attempt.Attempted.Task.WaitAsync(RaceTimeout);
        Assert.False(linking.IsCompleted);

        await transaction.CommitAsync();
        Assert.True((await linking).MethodLinked);
    }

    private WebApplicationFactory<Program> BlockRaceFactory(
        bool blockLocksFirst,
        PairLockGate holdingGate,
        PairLockAttemptGate attemptGate) =>
        _configured!.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFriendPairLocker>();
                services.AddScoped<FriendPairLocker>();
                services.AddScoped<IFriendPairLocker>(provider =>
                    blockLocksFirst
                        ? new HoldingFriendPairLocker(
                            provider.GetRequiredService<FriendPairLocker>(),
                            holdingGate)
                        : new AttemptingFriendPairLocker(
                            provider.GetRequiredService<FriendPairLocker>(),
                            attemptGate));
                services.RemoveAll<ISteamImportAccountGateway>();
                services.AddScoped<UserAccountGateway>();
                services.AddScoped<ISteamImportAccountGateway>(provider =>
                    blockLocksFirst
                        ? new AttemptingSteamImportAccountGateway(
                            provider.GetRequiredService<UserAccountGateway>(),
                            attemptGate)
                        : new HoldingSteamImportAccountGateway(
                            provider.GetRequiredService<UserAccountGateway>(),
                            holdingGate));
            }));

    private WebApplicationFactory<Program> ProjectionRaceFactory(
        OwnershipProjectionGate gate) =>
        _configured!.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamImportAccountGateway>();
                services.AddScoped<UserAccountGateway>();
                services.AddScoped<ISteamImportAccountGateway>(provider =>
                    new ProjectingSteamImportAccountGateway(
                        provider.GetRequiredService<UserAccountGateway>(),
                        gate));
            }));

    private WebApplicationFactory<Program> ImportFirstA030RaceFactory(
        PairLockGate holdingGate,
        UserLockAttemptState attempt) =>
        _configured!.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamImportAccountGateway>();
                services.AddScoped<UserAccountGateway>();
                services.AddScoped<ISteamImportAccountGateway>(provider =>
                    new HoldingSteamImportAccountGateway(
                        provider.GetRequiredService<UserAccountGateway>(),
                        holdingGate));
                services.RemoveAll<IUserRepository>();
                services.AddScoped<UserRepository>();
                services.AddScoped<IUserRepository>(provider =>
                    new AttemptingUserRepository(
                        provider.GetRequiredService<UserRepository>(),
                        attempt));
            }));

    private async Task<FriendEndpointUser> SeedA030LinkedAsync(string steamId64)
    {
        FriendEndpointUser user = await FriendEndpointTest.SeedAsync(_factory);
        Assert.True((await LinkSteamAsync(user.Id, steamId64, _configured!)).MethodLinked);
        return user;
    }

    private static async Task AssertBlockedAttemptAsync(
        Task second,
        PairLockAttemptGate attemptGate,
        PairLockGate holdingGate)
    {
        try
        {
            await attemptGate.Attempted.Task.WaitAsync(RaceTimeout);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            holdingGate.Release.TrySetResult();
        }
    }

    private static async Task<BlockGraphCommandResult> BlockAsync(
        FriendEndpointUser importer,
        FriendEndpointUser candidate,
        WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BlockUserUseCase>().ExecuteAsync(
            importer.Id,
            candidate.Username,
            default);
    }

    private static async Task<ImportSteamFriendsResult> ImportAsync(
        UserId importerId,
        WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<ImportSteamFriendsUseCase>()
            .ExecuteAsync(importerId, default);
    }

    private static async Task<ExternalSignInResult> LinkSteamAsync(
        UserId userId,
        string steamId64,
        WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<CompleteExternalSignInUseCase>()
            .ExecuteAsync(
                new CompleteExternalSignInCommand(
                    "steam",
                    steamId64,
                    null,
                    false,
                    ExternalStartMode.Link,
                    false,
                    userId,
                    userId),
                default);
    }

    private static async Task<UnlinkSteamResult> UnlinkSteamAsync(
        UserId userId,
        WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<UnlinkSteamUseCase>()
            .ExecuteAsync(
                new UnlinkSteamCommand(userId, UnlinkOrigin.AuthMethod, true),
                default);
    }

    private static async Task RelinkSteamAsync(
        UserId userId,
        string steamId64,
        WebApplicationFactory<Program> factory)
    {
        await UnlinkSteamSuccessfullyAsync(userId, factory);
        Assert.True((await LinkSteamAsync(userId, steamId64, factory)).MethodLinked);
    }

    private static async Task UnlinkSteamSuccessfullyAsync(
        UserId userId,
        WebApplicationFactory<Program> factory) =>
        Assert.True((await UnlinkSteamAsync(userId, factory)).Succeeded);

    private static async Task TransferSteamAsync(
        UserId oldOwnerId,
        UserId newOwnerId,
        string steamId64,
        WebApplicationFactory<Program> factory)
    {
        await UnlinkSteamSuccessfullyAsync(oldOwnerId, factory);
        Assert.True((await LinkSteamAsync(newOwnerId, steamId64, factory)).MethodLinked);
    }

    private async Task<RetryWriteSnapshot> RetrySnapshotAsync(params UserId[] userIds)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var versions = new List<(UserId Id, uint Xmin)>();
        foreach (UserId userId in userIds)
        {
            var row = await context.DomainUsers.AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => new
                {
                    user.Id,
                    Xmin = EF.Property<uint>(user, "xmin"),
                })
                .SingleAsync();
            versions.Add((row.Id, row.Xmin));
        }
        var identities = await context.SteamIdentities.AsNoTracking().ToArrayAsync();
        return new RetryWriteSnapshot(
            string.Join('|', versions
                .OrderBy(value => value.Id.Value)
                .Select(value => $"{value.Id.Value:D}:{value.Xmin}")),
            await context.DomainUsers.CountAsync(),
            await context.PlatformLinks.CountAsync(),
            identities.Length,
            string.Join('|', identities
                .OrderBy(value => value.UserId.Value)
                .Select(value => $"{value.UserId.Value:D}:{value.SteamId64}")),
            await context.Set<IdentityUserLogin<Guid>>().CountAsync(),
            await context.Friendships.CountAsync(),
            await context.Blocks.CountAsync(),
            await context.Notifications.CountAsync());
    }

    private static Task<int> SteamMatchCountAsync(
        AppDbContext context,
        UserId importerId,
        UserId candidateId) =>
        context.Notifications.CountAsync(notification =>
            notification.RecipientUserId == importerId &&
            notification.ActorUserId == candidateId &&
            notification.Type == NotificationType.SteamImportMatch);

    private static Task<string?> CurrentSteamIdAsync(AppDbContext context, UserId userId) =>
        context.SteamIdentities.AsNoTracking()
            .Where(identity => identity.UserId == userId)
            .Select(identity => identity.SteamId64)
            .SingleOrDefaultAsync();

    private static Task<UserId> CurrentSteamOwnerAsync(
        AppDbContext context,
        string steamId64) =>
        context.SteamIdentities.AsNoTracking()
            .Where(identity => identity.SteamId64 == steamId64)
            .Select(identity => identity.UserId)
            .SingleAsync();

    private sealed record RetryWriteSnapshot(
        string UserXmins,
        int Users,
        int PlatformLinks,
        int SteamIdentities,
        string SteamOwners,
        int AuthLogins,
        int Friendships,
        int Blocks,
        int Notifications);
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

internal sealed class StaticSteamProfileClient : ISteamProfileClient
{
    public Task<SteamProfile?> GetProfileAsync(
        string steamId64,
        CancellationToken cancellationToken) =>
        Task.FromResult<SteamProfile?>(new SteamProfile("Linked Steam", null));
}

internal sealed class OwnershipProjectionGate
{
    private int _projectionCalls;

    internal TaskCompletionSource<IReadOnlyDictionary<string, UserId>> FirstProjection { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource ReleaseFirst { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<IReadOnlyDictionary<string, UserId>> RetryProjection { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource ReleaseRetry { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int ProjectionCalls => Volatile.Read(ref _projectionCalls);

    internal int NextProjection() => Interlocked.Increment(ref _projectionCalls);
}

internal sealed class ProjectingSteamImportAccountGateway(
    ISteamImportAccountGateway inner,
    OwnershipProjectionGate gate) : ISteamImportAccountGateway
{
    public Task<string?> FindSteamIdAsync(UserId id, CancellationToken cancellationToken) =>
        inner.FindSteamIdAsync(id, cancellationToken);

    public async Task<IReadOnlyDictionary<string, UserId>> FindSteamIdentityOwnersAsync(
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, UserId> result =
            await inner.FindSteamIdentityOwnersAsync(steamIds, cancellationToken);
        int projection = gate.NextProjection();
        if (projection == 1)
        {
            gate.FirstProjection.TrySetResult(result);
            await gate.ReleaseFirst.Task.WaitAsync(cancellationToken);
        }
        else if (projection == 2)
        {
            gate.RetryProjection.TrySetResult(result);
            await gate.ReleaseRetry.Task.WaitAsync(cancellationToken);
        }

        return result;
    }

    public Task LockUsersAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken) =>
        inner.LockUsersAsync(userIds, cancellationToken);

    public Task<SteamImportOwnershipSnapshot> ReadSteamImportOwnershipSnapshotAsync(
        UserId importerId,
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken) =>
        inner.ReadSteamImportOwnershipSnapshotAsync(importerId, steamIds, cancellationToken);
}

internal sealed class HoldingSteamImportAccountGateway(
    ISteamImportAccountGateway inner,
    PairLockGate gate) : ISteamImportAccountGateway
{
    public Task<string?> FindSteamIdAsync(UserId id, CancellationToken cancellationToken) =>
        inner.FindSteamIdAsync(id, cancellationToken);

    public Task<IReadOnlyDictionary<string, UserId>> FindSteamIdentityOwnersAsync(
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken) =>
        inner.FindSteamIdentityOwnersAsync(steamIds, cancellationToken);

    public async Task LockUsersAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken)
    {
        await inner.LockUsersAsync(userIds, cancellationToken);
        gate.Held.TrySetResult();
        await gate.Release.Task.WaitAsync(cancellationToken);
    }

    public Task<SteamImportOwnershipSnapshot> ReadSteamImportOwnershipSnapshotAsync(
        UserId importerId,
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken) =>
        inner.ReadSteamImportOwnershipSnapshotAsync(importerId, steamIds, cancellationToken);
}

internal sealed class AttemptingSteamImportAccountGateway(
    ISteamImportAccountGateway inner,
    PairLockAttemptGate gate) : ISteamImportAccountGateway
{
    public Task<string?> FindSteamIdAsync(UserId id, CancellationToken cancellationToken) =>
        inner.FindSteamIdAsync(id, cancellationToken);

    public Task<IReadOnlyDictionary<string, UserId>> FindSteamIdentityOwnersAsync(
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken) =>
        inner.FindSteamIdentityOwnersAsync(steamIds, cancellationToken);

    public async Task LockUsersAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken)
    {
        Task attempt = inner.LockUsersAsync(userIds, cancellationToken);
        gate.Attempted.TrySetResult();
        await attempt;
    }

    public Task<SteamImportOwnershipSnapshot> ReadSteamImportOwnershipSnapshotAsync(
        UserId importerId,
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken) =>
        inner.ReadSteamImportOwnershipSnapshotAsync(importerId, steamIds, cancellationToken);
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
        UserId id,
        CancellationToken cancellationToken) =>
        inner.FindAsync(id, cancellationToken);

    public async Task<PartyUp.Domain.Users.User?> FindForUpdateAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        Task<PartyUp.Domain.Users.User?> attempt =
            inner.FindForUpdateAsync(id, cancellationToken);
        state.Attempted.TrySetResult();
        return await attempt;
    }

    public Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
        NodaTime.Instant now,
        int max,
        CancellationToken cancellationToken) =>
        inner.FindDueForDeletionAsync(now, max, cancellationToken);

    public void Add(PartyUp.Domain.Users.User user) => inner.Add(user);
}
