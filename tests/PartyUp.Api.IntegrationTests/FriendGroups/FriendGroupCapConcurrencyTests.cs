using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Api.IntegrationTests.Friends;
using PartyUp.Application.Abstractions;
using PartyUp.Application.FriendGroups;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Friendships;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.FriendGroups;

/// <summary>
/// D-208's two caps are app-checked reads, so only the row lock taken before the count makes them
/// safe under concurrency. Both transactions here run at READ COMMITTED (<c>UnitOfWork</c> takes no
/// explicit isolation level), so with the lock removed the second transaction would read the
/// pre-commit count of the first and admit a 51st group / 101st membership row. Each test overlaps
/// the two commands deliberately: the winner is held INSIDE its lock until the loser has issued its
/// own lock request, so the loser's count read is forced to happen while the winner is still
/// uncommitted — exactly the window the lock closes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FriendGroupCapConcurrencyTests : IAsyncLifetime
{
    private readonly FriendGraphDatabase _database = new();
    private readonly MutableClock _clock = new(Instant.FromUtc(2026, 8, 18, 12, 0));
    private User[] _users = [];

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _users = await _database.SeedUsersAsync(FriendGroup.MaxMembers + 2, _clock);
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    /// <summary>
    /// Owner-row lock (<c>IUserRepository.FindForUpdateAsync</c>) before
    /// <c>CountOwnedForUpdateAsync</c>: two 50th creates racing at 49 owned groups admit exactly one.
    /// </summary>
    [Fact]
    public async Task ConcurrentCreatesAtTheOwnedGroupCap_AdmitExactlyOne_D208()
    {
        UserId ownerId = _users[0].Id;
        await SeedOwnedGroupsAsync(ownerId, FriendGroupUseCases.MaxOwnedGroups - 1);

        var holdGate = new CapLockHoldGate();
        var attemptGate = new CapLockAttemptGate();
        await using AppDbContext winnerContext = _database.CreateContext();
        await using AppDbContext loserContext = _database.CreateContext();

        Task<FriendGroupCommandResult> winner = CreateUseCases(
                winnerContext,
                users: new HoldingUserRepository(new UserRepository(winnerContext), holdGate))
            .CreateAsync(ownerId, "Race winner group", default);
        await holdGate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task<FriendGroupCommandResult> loser = CreateUseCases(
                loserContext,
                users: new AttemptingUserRepository(new UserRepository(loserContext), attemptGate))
            .CreateAsync(ownerId, "Race loser group", default);
        await AssertBlockedThenReleaseAsync(loser, attemptGate, holdGate);

        Assert.True((await winner).Succeeded);
        Assert.Equal("group_limit_reached", (await loser).Code);

        await using AppDbContext verification = _database.CreateContext();
        Assert.Equal(
            FriendGroupUseCases.MaxOwnedGroups,
            await verification.FriendGroups.CountAsync(group => group.OwnerId == ownerId));
    }

    /// <summary>
    /// Group-row lock (<c>IFriendGroupRepository.LoadForUpdateAsync</c>, <c>FOR NO KEY UPDATE</c>)
    /// before the roster count: two invites racing at 99 membership rows admit exactly one.
    /// </summary>
    [Fact]
    public async Task ConcurrentInvitesAtTheMembershipRowCap_AdmitExactlyOne_D208()
    {
        UserId ownerId = _users[0].Id;
        User firstInvitee = _users[^2];
        User secondInvitee = _users[^1];
        FriendGroupId groupId = await SeedFullMinusOneGroupAsync(ownerId, [firstInvitee, secondInvitee]);

        var holdGate = new CapLockHoldGate();
        var attemptGate = new CapLockAttemptGate();
        await using AppDbContext winnerContext = _database.CreateContext();
        await using AppDbContext loserContext = _database.CreateContext();

        Task<FriendGroupCommandResult> winner = CreateUseCases(
                winnerContext,
                groups: new HoldingFriendGroupRepository(
                    new FriendGroupRepository(winnerContext),
                    holdGate))
            .InviteAsync(ownerId, groupId, [firstInvitee.Id], default);
        await holdGate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task<FriendGroupCommandResult> loser = CreateUseCases(
                loserContext,
                groups: new AttemptingFriendGroupRepository(
                    new FriendGroupRepository(loserContext),
                    attemptGate))
            .InviteAsync(ownerId, groupId, [secondInvitee.Id], default);
        await AssertBlockedThenReleaseAsync(loser, attemptGate, holdGate);

        Assert.True((await winner).Succeeded);
        Assert.Equal("group_member_limit_reached", (await loser).Code);

        await using AppDbContext verification = _database.CreateContext();
        FriendGroup persisted = await verification.FriendGroups
            .AsNoTracking()
            .Include(group => group.Members)
            .SingleAsync(group => group.Id == groupId);
        Assert.Equal(FriendGroup.MaxMembers, persisted.Members.Count);
        Assert.Contains(persisted.Members, member => member.UserId == firstInvitee.Id);
        Assert.DoesNotContain(persisted.Members, member => member.UserId == secondInvitee.Id);
    }

    private FriendGroupUseCases CreateUseCases(
        AppDbContext context,
        IUserRepository? users = null,
        IFriendGroupRepository? groups = null) =>
        new(
            groups ?? new FriendGroupRepository(context),
            users ?? new UserRepository(context),
            new FriendshipRepository(context),
            new BlockRepository(context),
            new NotificationRepository(context),
            new UnitOfWork(context),
            _clock);

    private async Task SeedOwnedGroupsAsync(UserId ownerId, int count)
    {
        await using AppDbContext context = _database.CreateContext();
        for (int index = 0; index < count; index++)
        {
            FriendGroup group = FriendGroup.Create(
                ownerId,
                GroupName.Create(FormattableString.Invariant($"Seeded group {index}")),
                _clock);
            _ = group.DequeueEvents();
            context.FriendGroups.Add(group);
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// A group holding <c>MaxMembers - 1</c> accepted rows, plus an accepted friendship between the
    /// owner and each pending invitee so <c>InviteAsync</c>'s friend check passes for both racers.
    /// </summary>
    private async Task<FriendGroupId> SeedFullMinusOneGroupAsync(UserId ownerId, User[] invitees)
    {
        FriendGroup group = FriendGroup.Create(ownerId, GroupName.Create("Full minus one"), _clock);
        User[] members = [.. _users.Skip(1).Take(FriendGroup.MaxMembers - 1)];
        foreach (User member in members)
        {
            group.Invite(ownerId, member.Id, isAcceptedFriendOfOwner: true, _clock);
            group.AcceptInvite(member.Id, _clock);
        }

        _ = group.DequeueEvents();

        await using AppDbContext context = _database.CreateContext();
        context.FriendGroups.Add(group);
        foreach (User invitee in invitees)
        {
            Friendship friendship = Friendship.Request(_users[0], invitee, _clock);
            friendship.Accept(invitee.Id, _clock);
            _ = friendship.DequeueEvents();
            context.Friendships.Add(friendship);
        }

        await context.SaveChangesAsync();
        return group.Id;
    }

    /// <summary>
    /// The loser has issued its lock request and must still be waiting on it: proof the winner's row
    /// lock is held and serializes the two count reads.
    /// </summary>
    private static async Task AssertBlockedThenReleaseAsync(
        Task loser,
        CapLockAttemptGate attemptGate,
        CapLockHoldGate holdGate)
    {
        try
        {
            await attemptGate.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(loser.IsCompleted);
        }
        finally
        {
            holdGate.Release.TrySetResult();
        }
    }
}

internal sealed class CapLockHoldGate
{
    internal TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class CapLockAttemptGate
{
    internal TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class HoldingUserRepository(IUserRepository inner, CapLockHoldGate gate) : IUserRepository
{
    public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken) =>
        inner.FindAsync(id, cancellationToken);

    public async Task<User?> FindForUpdateAsync(UserId id, CancellationToken cancellationToken)
    {
        User? user = await inner.FindForUpdateAsync(id, cancellationToken);
        gate.Held.TrySetResult();
        await gate.Release.Task.WaitAsync(cancellationToken);
        return user;
    }

    public Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
        Instant now,
        int max,
        CancellationToken cancellationToken) =>
        inner.FindDueForDeletionAsync(now, max, cancellationToken);

    public void Add(User user) => inner.Add(user);
}

internal sealed class AttemptingUserRepository(IUserRepository inner, CapLockAttemptGate gate) : IUserRepository
{
    public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken) =>
        inner.FindAsync(id, cancellationToken);

    public Task<User?> FindForUpdateAsync(UserId id, CancellationToken cancellationToken)
    {
        Task<User?> attempt = inner.FindForUpdateAsync(id, cancellationToken);
        gate.Attempted.TrySetResult();
        return attempt;
    }

    public Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
        Instant now,
        int max,
        CancellationToken cancellationToken) =>
        inner.FindDueForDeletionAsync(now, max, cancellationToken);

    public void Add(User user) => inner.Add(user);
}

internal abstract class DelegatingFriendGroupRepository(IFriendGroupRepository inner) : IFriendGroupRepository
{
    protected IFriendGroupRepository Inner => inner;

    public void Add(FriendGroup group) => inner.Add(group);

    public void Remove(FriendGroup group) => inner.Remove(group);

    public Task<FriendGroup?> FindNoTrackingAsync(FriendGroupId id, CancellationToken cancellationToken) =>
        inner.FindNoTrackingAsync(id, cancellationToken);

    public abstract Task<FriendGroup?> LoadForUpdateAsync(FriendGroupId id, CancellationToken cancellationToken);

    public Task<int> CountOwnedForUpdateAsync(UserId ownerId, CancellationToken cancellationToken) =>
        inner.CountOwnedForUpdateAsync(ownerId, cancellationToken);

    public Task<IReadOnlyCollection<FriendGroupId>> LockViewerGroupIdsAsync(
        UserId viewerId,
        IReadOnlyCollection<FriendGroupId> candidateGroupIds,
        CancellationToken cancellationToken) =>
        inner.LockViewerGroupIdsAsync(viewerId, candidateGroupIds, cancellationToken);

    public Task<IReadOnlyCollection<FriendGroupId>> GetViewerGroupIdsAsync(
        UserId viewerId,
        IReadOnlyCollection<FriendGroupId> candidateGroupIds,
        CancellationToken cancellationToken) =>
        inner.GetViewerGroupIdsAsync(viewerId, candidateGroupIds, cancellationToken);

    public Task<GroupName?> FindNameAsync(FriendGroupId id, CancellationToken cancellationToken) =>
        inner.FindNameAsync(id, cancellationToken);

    public Task<IReadOnlyCollection<FriendGroup>> ListInvolvingAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        inner.ListInvolvingAsync(userId, cancellationToken);

    public Task<IReadOnlyCollection<FriendGroup>> ListOwnedContainingAsync(
        UserId ownerId,
        UserId memberId,
        CancellationToken cancellationToken) =>
        inner.ListOwnedContainingAsync(ownerId, memberId, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, UserNameplate>> FindNameplatesAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken) =>
        inner.FindNameplatesAsync(userIds, cancellationToken);

    public Task<IReadOnlyCollection<UserId>> ListDistinctOrganizersTargetingAsync(
        FriendGroupId groupId,
        CancellationToken cancellationToken) =>
        inner.ListDistinctOrganizersTargetingAsync(groupId, cancellationToken);
}

internal sealed class HoldingFriendGroupRepository(IFriendGroupRepository inner, CapLockHoldGate gate)
    : DelegatingFriendGroupRepository(inner)
{
    public override async Task<FriendGroup?> LoadForUpdateAsync(
        FriendGroupId id,
        CancellationToken cancellationToken)
    {
        FriendGroup? group = await Inner.LoadForUpdateAsync(id, cancellationToken);
        gate.Held.TrySetResult();
        await gate.Release.Task.WaitAsync(cancellationToken);
        return group;
    }
}

internal sealed class AttemptingFriendGroupRepository(IFriendGroupRepository inner, CapLockAttemptGate gate)
    : DelegatingFriendGroupRepository(inner)
{
    public override Task<FriendGroup?> LoadForUpdateAsync(
        FriendGroupId id,
        CancellationToken cancellationToken)
    {
        Task<FriendGroup?> attempt = Inner.LoadForUpdateAsync(id, cancellationToken);
        gate.Attempted.TrySetResult();
        return attempt;
    }
}
