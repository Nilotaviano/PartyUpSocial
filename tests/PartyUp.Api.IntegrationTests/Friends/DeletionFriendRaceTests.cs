using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Deletion;
using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class DeletionFriendRaceTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraceExpiryRacingSend_LeavesNoGraphRow_D092(bool deletionLocksFirst)
    {
        (UserId actorId, UserId subjectId, string subjectUsername) =
            await SeedSendRacePairAsync();
        _factory.Clock.Advance(User.DeletionGracePeriod);
        await using AsyncServiceScope firstScope = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = _factory.Services.CreateAsyncScope();

        if (deletionLocksFirst)
        {
            var deletionGate = new DeletionLockGate();
            var provider = new HoldingDeletionScopeProvider(
                firstScope.ServiceProvider.GetRequiredService<IDeletionScopeProvider>(),
                deletionGate);
            ExecuteDeletionForUserUseCase deletion = Phase3DeletionTestSupport.CreateUseCase(
                firstScope.ServiceProvider,
                scopeProvider: provider);
            Task<bool> delete = deletion.ExecuteAsync(subjectId, default);
            await deletionGate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var attemptGate = new PairLockAttemptGate();
            AppDbContext sendContext = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Task<FriendGraphCommandResult> send = FriendCommandIntegration.SendAsync(
                sendContext,
                new AttemptingFriendPairLocker(
                    new FriendPairLocker(sendContext),
                    attemptGate),
                actorId,
                subjectUsername,
                _factory.Clock);
            await attemptGate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(send.IsCompleted);
            deletionGate.Release.TrySetResult();

            Assert.True(await delete);
            Assert.Equal("user_not_found", (await send).Code);
        }
        else
        {
            var pairGate = new PairLockGate();
            AppDbContext sendContext = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Task<FriendGraphCommandResult> send = FriendCommandIntegration.SendAsync(
                sendContext,
                new HoldingFriendPairLocker(new FriendPairLocker(sendContext), pairGate),
                actorId,
                subjectUsername,
                _factory.Clock);
            await pairGate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var attemptGate = new DeletionLockAttemptGate();
            var users = new AttemptingDeletionUserRepository(
                secondScope.ServiceProvider.GetRequiredService<IUserRepository>(),
                attemptGate);
            ExecuteDeletionForUserUseCase deletion = Phase3DeletionTestSupport.CreateUseCase(
                secondScope.ServiceProvider,
                users: users);
            Task<bool> delete = deletion.ExecuteAsync(subjectId, default);
            await attemptGate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(delete.IsCompleted);
            pairGate.Release.TrySetResult();

            Assert.True((await send).Succeeded);
            Assert.True(await delete);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True((await context.DomainUsers.AsNoTracking()
            .SingleAsync(user => user.Id == subjectId)).IsTombstoned);
        Assert.Equal(0, await context.Friendships.CountAsync());
        Assert.Equal(0, await context.Notifications.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraceExpiryRacingUnfriend_LeavesNoGraphRow_D092(bool deletionLocksFirst)
    {
        (UserId actorId, UserId subjectId, FriendshipId friendshipId) = await SeedRacePairAsync();
        _factory.Clock.Advance(User.DeletionGracePeriod);
        await using AsyncServiceScope firstScope = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = _factory.Services.CreateAsyncScope();

        if (deletionLocksFirst)
        {
            var deletionGate = new DeletionLockGate();
            var provider = new HoldingDeletionScopeProvider(
                firstScope.ServiceProvider.GetRequiredService<IDeletionScopeProvider>(),
                deletionGate);
            ExecuteDeletionForUserUseCase deletion = Phase3DeletionTestSupport.CreateUseCase(
                firstScope.ServiceProvider,
                scopeProvider: provider);
            Task<bool> delete = deletion.ExecuteAsync(subjectId, default);
            await deletionGate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var attemptGate = new PairLockAttemptGate();
            AppDbContext unfriendContext = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Task<FriendGraphCommandResult> unfriend = FriendCommandIntegration.UnfriendAsync(
                unfriendContext,
                new AttemptingFriendPairLocker(
                    new FriendPairLocker(unfriendContext),
                    attemptGate),
                actorId,
                friendshipId);
            await attemptGate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(unfriend.IsCompleted);
            deletionGate.Release.TrySetResult();

            Assert.True(await delete);
            Assert.Equal("friendship_not_found", (await unfriend).Code);
        }
        else
        {
            var pairGate = new PairLockGate();
            AppDbContext sendContext = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Task<FriendGraphCommandResult> unfriend = FriendCommandIntegration.UnfriendAsync(
                sendContext,
                new HoldingFriendPairLocker(new FriendPairLocker(sendContext), pairGate),
                actorId,
                friendshipId);
            await pairGate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var attemptGate = new DeletionLockAttemptGate();
            var users = new AttemptingDeletionUserRepository(
                secondScope.ServiceProvider.GetRequiredService<IUserRepository>(),
                attemptGate);
            ExecuteDeletionForUserUseCase deletion = Phase3DeletionTestSupport.CreateUseCase(
                secondScope.ServiceProvider,
                users: users);
            Task<bool> delete = deletion.ExecuteAsync(subjectId, default);
            await attemptGate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(delete.IsCompleted);
            pairGate.Release.TrySetResult();

            Assert.True((await unfriend).Succeeded);
            Assert.True(await delete);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True((await context.DomainUsers.AsNoTracking()
            .SingleAsync(user => user.Id == subjectId)).IsTombstoned);
        Assert.Equal(0, await context.Friendships.CountAsync());
        Assert.Equal(0, await context.Notifications.CountAsync());
    }

    [Fact]
    public async Task TwoMembersExpireConcurrently_BothSucceedAndDeleteSharedRowsOnce_D092()
    {
        (UserId firstId, UserId secondId) = await SeedExpiringPairAsync();
        _factory.Clock.Advance(User.DeletionGracePeriod);
        await using AsyncServiceScope firstScope = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = _factory.Services.CreateAsyncScope();
        var gate = new DeletionLockGate();
        var attemptGate = new DeletionLockAttemptGate();
        var counts = new DeletionApplyCounts();
        var firstProvider = new HoldingDeletionScopeProvider(
            firstScope.ServiceProvider.GetRequiredService<IDeletionScopeProvider>(),
            gate);
        var secondProvider = new AttemptingDeletionScopeProvider(
            secondScope.ServiceProvider.GetRequiredService<IDeletionScopeProvider>(),
            attemptGate);
        var firstApplier = new CountingDeletionResultApplier(
            firstScope.ServiceProvider.GetRequiredService<IDeletionResultApplier>(),
            counts);
        var secondApplier = new CountingDeletionResultApplier(
            secondScope.ServiceProvider.GetRequiredService<IDeletionResultApplier>(),
            counts);
        ExecuteDeletionForUserUseCase first = Phase3DeletionTestSupport.CreateUseCase(
            firstScope.ServiceProvider,
            scopeProvider: firstProvider,
            resultApplier: firstApplier);
        ExecuteDeletionForUserUseCase second = Phase3DeletionTestSupport.CreateUseCase(
            secondScope.ServiceProvider,
            scopeProvider: secondProvider,
            resultApplier: secondApplier);

        Task<bool> firstDelete = first.ExecuteAsync(firstId, default);
        await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<bool> secondDelete = second.ExecuteAsync(secondId, default);
        await attemptGate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(secondDelete.IsCompleted);
        gate.Release.TrySetResult();

        Assert.True(await firstDelete);
        Assert.True(await secondDelete);
        Assert.Equal(1, counts.Friendships);
        Assert.Equal(2, counts.Blocks);
        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await context.Friendships.CountAsync());
        Assert.Equal(0, await context.Blocks.CountAsync());
        Assert.All(
            await context.DomainUsers.AsNoTracking()
                .Where(user => user.Id == firstId || user.Id == secondId)
                .ToListAsync(),
            user => Assert.True(user.IsTombstoned));
        IReadOnlyList<DeletionJournalEntry> journal = await verification.ServiceProvider
            .GetRequiredService<IDeletionJournal>()
            .ReadSinceAsync(Instant.MinValue, 100, default);
        Assert.Contains(journal, entry => entry.UserId == firstId);
        Assert.Contains(journal, entry => entry.UserId == secondId);
    }

    private async Task<(UserId ActorId, UserId SubjectId, string SubjectUsername)>
        SeedSendRacePairAsync()
    {
        string suffix = Guid.NewGuid().ToString("N");
        UserId actorId = await _factory.SeedUserAsync(
            email: $"send-race-actor-{suffix}@example.test");
        UserId subjectId = await _factory.SeedUserAsync(
            email: $"send-race-subject-{suffix}@example.test");
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User actor = await context.DomainUsers.SingleAsync(user => user.Id == actorId);
        User subject = await context.DomainUsers.SingleAsync(user => user.Id == subjectId);
        actor.ChangeUsername(Username.Create($"senda{suffix[..12]}"), _factory.Clock);
        subject.ChangeUsername(Username.Create($"sends{suffix[..12]}"), _factory.Clock);
        context.UsernameRegistry.AddRange(
            UsernameRegistryEntry.ClaimNew(actor.Username!.Value, actor.Id),
            UsernameRegistryEntry.ClaimNew(subject.Username!.Value, subject.Id));
        subject.RequestDeletion(_factory.Clock);
        await context.SaveChangesAsync();
        return (actor.Id, subject.Id, subject.Username.Value.Value);
    }

    private async Task<(UserId ActorId, UserId SubjectId, FriendshipId FriendshipId)>
        SeedRacePairAsync()
    {
        string suffix = Guid.NewGuid().ToString("N");
        UserId actorId = await _factory.SeedUserAsync(
            email: $"race-actor-{suffix}@example.test");
        UserId subjectId = await _factory.SeedUserAsync(
            email: $"race-subject-{suffix}@example.test");
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User actor = await context.DomainUsers.SingleAsync(user => user.Id == actorId);
        User subject = await context.DomainUsers.SingleAsync(user => user.Id == subjectId);
        actor.ChangeUsername(Username.Create($"racea{suffix[..12]}"), _factory.Clock);
        subject.ChangeUsername(Username.Create($"races{suffix[..12]}"), _factory.Clock);
        context.UsernameRegistry.Add(
            UsernameRegistryEntry.ClaimNew(subject.Username!.Value, subject.Id));
        Friendship friendship = Friendship.Request(actor, subject, _factory.Clock);
        friendship.Accept(subject.Id, _factory.Clock);
        _ = friendship.DequeueEvents();
        context.Friendships.Add(friendship);
        subject.RequestDeletion(_factory.Clock);
        await context.SaveChangesAsync();
        return (actor.Id, subject.Id, friendship.Id);
    }

    private async Task<(UserId FirstId, UserId SecondId)> SeedExpiringPairAsync()
    {
        string suffix = Guid.NewGuid().ToString("N");
        UserId firstId = await _factory.SeedUserAsync(
            email: $"expire-first-{suffix}@example.test");
        UserId secondId = await _factory.SeedUserAsync(
            email: $"expire-second-{suffix}@example.test");
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User first = await context.DomainUsers.SingleAsync(user => user.Id == firstId);
        User second = await context.DomainUsers.SingleAsync(user => user.Id == secondId);
        first.ChangeUsername(Username.Create($"expiref{suffix[..12]}"), _factory.Clock);
        second.ChangeUsername(Username.Create($"expires{suffix[..12]}"), _factory.Clock);
        UsernameRegistryEntry firstUsername =
            UsernameRegistryEntry.ClaimNew(first.Username!.Value, first.Id);
        UsernameRegistryEntry secondUsername =
            UsernameRegistryEntry.ClaimNew(second.Username!.Value, second.Id);
        Assert.NotEqual(firstUsername.NameCanonical, secondUsername.NameCanonical);
        context.UsernameRegistry.AddRange(firstUsername, secondUsername);
        Friendship friendship = Friendship.Request(first, second, _factory.Clock);
        friendship.Accept(second.Id, _factory.Clock);
        _ = friendship.DequeueEvents();
        context.Friendships.Add(friendship);
        context.Blocks.AddRange(
            Block.Create(first.Id, second.Id, second.Username!.Value, _factory.Clock),
            Block.Create(second.Id, first.Id, first.Username!.Value, _factory.Clock));
        first.RequestDeletion(_factory.Clock);
        second.RequestDeletion(_factory.Clock);
        await context.SaveChangesAsync();
        return (first.Id, second.Id);
    }

}

internal sealed class DeletionLockGate
{
    internal TaskCompletionSource Held { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class DeletionLockAttemptGate
{
    internal TaskCompletionSource Attempted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class HoldingDeletionScopeProvider(
    IDeletionScopeProvider inner,
    DeletionLockGate gate) : IDeletionScopeProvider
{
    public async Task<DeletionScope> LoadAsync(UserId id, CancellationToken cancellationToken)
    {
        DeletionScope scope = await inner.LoadAsync(id, cancellationToken);
        gate.Held.TrySetResult();
        await gate.Release.Task.WaitAsync(cancellationToken);
        return scope;
    }
}

internal sealed class AttemptingDeletionScopeProvider(
    IDeletionScopeProvider inner,
    DeletionLockAttemptGate gate) : IDeletionScopeProvider
{
    public async Task<DeletionScope> LoadAsync(UserId id, CancellationToken cancellationToken)
    {
        Task<DeletionScope> attempt = inner.LoadAsync(id, cancellationToken);
        gate.Attempted.TrySetResult();
        return await attempt;
    }
}

internal sealed class AttemptingDeletionUserRepository(
    IUserRepository inner,
    DeletionLockAttemptGate gate) : IUserRepository
{
    public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken) =>
        inner.FindAsync(id, cancellationToken);

    public async Task<User?> FindForUpdateAsync(UserId id, CancellationToken cancellationToken)
    {
        Task<User?> attempt = inner.FindForUpdateAsync(id, cancellationToken);
        gate.Attempted.TrySetResult();
        return await attempt;
    }

    public Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
        Instant now,
        int max,
        CancellationToken cancellationToken) =>
        inner.FindDueForDeletionAsync(now, max, cancellationToken);

    public void Add(User user) => inner.Add(user);
}

internal sealed class DeletionApplyCounts
{
    private int _friendships;
    private int _blocks;

    internal int Friendships => Volatile.Read(ref _friendships);

    internal int Blocks => Volatile.Read(ref _blocks);

    internal void Add(DeletionExecutionResult result)
    {
        Interlocked.Add(ref _friendships, result.DeletedFriendships.Count);
        Interlocked.Add(ref _blocks, result.DeletedBlocks.Count);
    }
}

internal sealed class CountingDeletionResultApplier(
    IDeletionResultApplier inner,
    DeletionApplyCounts counts) : IDeletionResultApplier
{
    public Task ApplyAsync(
        DeletionExecutionResult result,
        UserId subject,
        CancellationToken cancellationToken)
    {
        counts.Add(result);
        return inner.ApplyAsync(result, subject, cancellationToken);
    }
}
