using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.FriendGroups;
using PartyUp.Application.Polls;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.FriendGroups;

/// <summary>
/// The roster-identity fan-out closing the group-roster identity gap: <see cref="FriendGroupReadUseCases.GetAsync"/>
/// now resolves nameplates for the owner and every member and applies D-076 masking against the
/// VIEWER's block graph, instead of leaving identity resolution to a client-side friends join.
/// </summary>
public sealed class FriendGroupReadUseCasesTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 16, 12, 0);

    [Fact]
    public async Task RetainedNonFriendMember_StillGetsANameOnTheOwnersRoster_D200()
    {
        // The member is Accepted but no longer a friend of the owner (D-200: membership survives
        // unfriending). Nothing blocks them, so the owner still reads a real nameplate for them,
        // never a masked/anonymous row.
        var owner = UserId.New();
        var member = UserId.New();
        FriendGroup group = BuildGroup(owner, [(member, accept: true)]);

        var groups = new FakeFriendGroupRepository();
        groups.Items.Add(group);
        groups.Nameplates[member.Value] = new UserNameplate(DisplayName.Create("Retained Rae"), Avatar.Fallback);

        var useCase = new FriendGroupReadUseCases(groups, new FakeBlockRepository());

        FriendGroupDetail? detail = await useCase.GetAsync(owner, group.Id, CancellationToken.None);

        Assert.NotNull(detail);
        FriendGroupMemberView row = Assert.Single(detail!.Members!);
        Assert.False(row.Masked);
        Assert.Equal("Retained Rae", row.DisplayName!.Value.Value);
        Assert.NotNull(row.Avatar);
    }

    [Fact]
    public async Task BlockedCounterpartMember_RendersMaskedButTheRowStaysListed_D202()
    {
        // A block between two NON-OWNER members leaves both membership rows in place (D-202) — the
        // masking is render-time only, scoped to the VIEWER's own block graph.
        var owner = UserId.New();
        var viewer = UserId.New();
        var blockedCounterpart = UserId.New();
        FriendGroup group = BuildGroup(owner, [(viewer, accept: true), (blockedCounterpart, accept: true)]);

        var groups = new FakeFriendGroupRepository();
        groups.Items.Add(group);
        groups.Nameplates[blockedCounterpart.Value] =
            new UserNameplate(DisplayName.Create("Blocked Bea"), Avatar.Fallback);

        var blocks = new FakeBlockRepository();
        blocks.Pairs.Add((viewer, blockedCounterpart));

        var useCase = new FriendGroupReadUseCases(groups, blocks);

        FriendGroupDetail? detail = await useCase.GetAsync(viewer, group.Id, CancellationToken.None);

        Assert.NotNull(detail);
        FriendGroupMemberView row = Assert.Single(detail!.Members!, m => m.UserId == blockedCounterpart);
        Assert.True(row.Masked);
        Assert.Null(row.DisplayName);
        Assert.Null(row.Avatar);

        // Not removed — the row is still listed, D-202's "coexist, masked at render time" posture.
        Assert.Contains(detail.Members!, m => m.UserId == blockedCounterpart);
    }

    [Fact]
    public async Task OwnerNameplateRoutesThroughTheSameMaskingPath_NeverSpecialCased_D202()
    {
        var owner = UserId.New();
        var member = UserId.New();
        FriendGroup group = BuildGroup(owner, [(member, accept: true)]);

        var groups = new FakeFriendGroupRepository();
        groups.Items.Add(group);
        groups.Nameplates[owner.Value] = new UserNameplate(DisplayName.Create("Owner Olive"), Avatar.Fallback);

        var useCase = new FriendGroupReadUseCases(groups, new FakeBlockRepository());

        FriendGroupDetail? detail = await useCase.GetAsync(member, group.Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.False(detail!.OwnerMasked);
        Assert.Equal("Owner Olive", detail.OwnerDisplayName!.Value.Value);
    }

    [Fact]
    public async Task InvitedOnlyViewer_ResolvesNoIdentitiesAtAll_D199()
    {
        var owner = UserId.New();
        var invitedViewer = UserId.New();
        FriendGroup group = BuildGroup(owner, [(invitedViewer, accept: false)]);

        var groups = new FakeFriendGroupRepository();
        groups.Items.Add(group);

        var useCase = new FriendGroupReadUseCases(groups, new FakeBlockRepository());

        FriendGroupDetail? detail = await useCase.GetAsync(invitedViewer, group.Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Null(detail!.Members);
        Assert.Null(detail.OwnerDisplayName);
        Assert.False(detail.OwnerMasked);
    }

    [Fact]
    public async Task ListMineAsync_ReturnsOwnedMemberOfAndInviteRowsWithOwnerNameplates_D202()
    {
        var actor = UserId.New();
        var otherOwner = UserId.New();
        var thirdOwner = UserId.New();

        FriendGroup owned = FriendGroup.Create(actor, GroupName.Create("Owned by me"), new TestClock(Now));
        FriendGroup memberOfGroup = BuildGroup(otherOwner, [(actor, accept: true)]);
        FriendGroup inviteGroup = BuildGroup(thirdOwner, [(actor, accept: false)]);

        var groups = new FakeFriendGroupRepository();
        groups.Items.Add(owned);
        groups.Items.Add(memberOfGroup);
        groups.Items.Add(inviteGroup);
        groups.Nameplates[otherOwner.Value] = new UserNameplate(DisplayName.Create("Owner One"), Avatar.Fallback);
        groups.Nameplates[thirdOwner.Value] = new UserNameplate(DisplayName.Create("Owner Two"), Avatar.Fallback);

        var useCase = new FriendGroupReadUseCases(groups, new FakeBlockRepository());

        FriendGroupListResult result = await useCase.ListMineAsync(actor, CancellationToken.None);

        FriendGroupSummary ownedSummary = Assert.Single(result.Owned);
        Assert.Equal(owned.Id, ownedSummary.Id);

        FriendGroupSummary memberOfSummary = Assert.Single(result.MemberOf);
        Assert.Equal(memberOfGroup.Id, memberOfSummary.Id);
        Assert.False(memberOfSummary.OwnerMasked);
        Assert.Equal("Owner One", memberOfSummary.OwnerDisplayName!.Value.Value);
        Assert.NotNull(memberOfSummary.OwnerAvatar);

        FriendGroupInviteSummary inviteSummary = Assert.Single(result.Invites);
        Assert.Equal(inviteGroup.Id, inviteSummary.Id);
        Assert.False(inviteSummary.OwnerMasked);
        Assert.Equal("Owner Two", inviteSummary.OwnerDisplayName!.Value.Value);
    }

    [Fact]
    public async Task ListMineAsync_MasksAMemberOfOwnerTheActorHasBlocked_D202()
    {
        var actor = UserId.New();
        var blockedOwner = UserId.New();
        FriendGroup memberOfGroup = BuildGroup(blockedOwner, [(actor, accept: true)]);

        var groups = new FakeFriendGroupRepository();
        groups.Items.Add(memberOfGroup);
        groups.Nameplates[blockedOwner.Value] = new UserNameplate(DisplayName.Create("Blocked Owner"), Avatar.Fallback);

        var blocks = new FakeBlockRepository();
        blocks.Pairs.Add((actor, blockedOwner));

        var useCase = new FriendGroupReadUseCases(groups, blocks);

        FriendGroupListResult result = await useCase.ListMineAsync(actor, CancellationToken.None);

        FriendGroupSummary memberOfSummary = Assert.Single(result.MemberOf);
        Assert.True(memberOfSummary.OwnerMasked);
        Assert.Null(memberOfSummary.OwnerDisplayName);
        Assert.Null(memberOfSummary.OwnerAvatar);
    }

    [Fact]
    public async Task ListMineAsync_MasksTheOwnerOnABlockedInviteToo_D202()
    {
        var actor = UserId.New();
        var blockedOwner = UserId.New();
        FriendGroup inviteGroup = BuildGroup(blockedOwner, [(actor, accept: false)]);

        var groups = new FakeFriendGroupRepository();
        groups.Items.Add(inviteGroup);
        groups.Nameplates[blockedOwner.Value] = new UserNameplate(DisplayName.Create("Blocked Owner"), Avatar.Fallback);

        var blocks = new FakeBlockRepository();
        blocks.Pairs.Add((actor, blockedOwner));

        var useCase = new FriendGroupReadUseCases(groups, blocks);

        FriendGroupListResult result = await useCase.ListMineAsync(actor, CancellationToken.None);

        FriendGroupInviteSummary inviteSummary = Assert.Single(result.Invites);
        Assert.True(inviteSummary.OwnerMasked);
        Assert.Null(inviteSummary.OwnerDisplayName);
        Assert.Null(inviteSummary.OwnerAvatar);
    }

    private static FriendGroup BuildGroup(UserId owner, IReadOnlyList<(UserId member, bool accept)> members)
    {
        var clock = new TestClock(Now);
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("Roster fixture"), clock);
        foreach ((UserId member, bool accept) in members)
        {
            group.Invite(owner, member, isAcceptedFriendOfOwner: true, clock);
            if (accept)
            {
                group.AcceptInvite(member, clock);
            }
        }

        return group;
    }
}

internal sealed class FakeFriendGroupRepository : IFriendGroupRepository
{
    internal List<FriendGroup> Items { get; } = [];

    internal Dictionary<Guid, UserNameplate> Nameplates { get; } = [];

    public void Add(FriendGroup group) => Items.Add(group);

    public void Remove(FriendGroup group) => Items.Remove(group);

    public Task<FriendGroup?> FindNoTrackingAsync(FriendGroupId id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(g => g.Id == id));

    public Task<FriendGroup?> LoadForUpdateAsync(FriendGroupId id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(g => g.Id == id));

    public Task<int> CountOwnedForUpdateAsync(UserId ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Count(g => g.OwnerId == ownerId));

    public Task<IReadOnlyCollection<FriendGroupId>> LockViewerGroupIdsAsync(
        UserId viewerId, IReadOnlyCollection<FriendGroupId> candidateGroupIds, CancellationToken cancellationToken) =>
        Task.FromResult(ViewerGroupIds(viewerId, candidateGroupIds));

    public Task<IReadOnlyCollection<FriendGroupId>> GetViewerGroupIdsAsync(
        UserId viewerId, IReadOnlyCollection<FriendGroupId> candidateGroupIds, CancellationToken cancellationToken) =>
        Task.FromResult(ViewerGroupIds(viewerId, candidateGroupIds));

    public Task<GroupName?> FindNameAsync(FriendGroupId id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(g => g.Id == id)?.Name);

    public Task<IReadOnlyCollection<FriendGroup>> ListInvolvingAsync(UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<FriendGroup>>([.. Items.Where(g => g.Involves(userId))]);

    public Task<IReadOnlyCollection<UserId>> ListDistinctOrganizersTargetingAsync(
        FriendGroupId groupId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<UserId>>([]);

    public Task<IReadOnlyDictionary<Guid, UserNameplate>> FindNameplatesAsync(
        IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken)
    {
        Dictionary<Guid, UserNameplate> result = [];
        foreach (UserId id in userIds)
        {
            if (Nameplates.TryGetValue(id.Value, out UserNameplate? nameplate))
            {
                result[id.Value] = nameplate;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, UserNameplate>>(result);
    }

    private IReadOnlyCollection<FriendGroupId> ViewerGroupIds(
        UserId viewerId, IReadOnlyCollection<FriendGroupId> candidateGroupIds) =>
        [.. candidateGroupIds.Where(id => Items.Any(g =>
            g.Id == id && (g.OwnerId == viewerId || g.HasAcceptedMember(viewerId))))];
}

/// <summary>A minimal block store keyed on unordered pairs — direction never matters for masking (D-065).</summary>
internal sealed class FakeBlockRepository : IBlockRepository
{
    internal List<(UserId First, UserId Second)> Pairs { get; } = [];

    public Task<IReadOnlyList<Block>> FindPairForUpdateAsync(
        UserId first, UserId second, CancellationToken cancellationToken) =>
        FindPairAsync(first, second, cancellationToken);

    public Task<IReadOnlyList<Block>> FindPairAsync(
        UserId first, UserId second, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Block>>(HasPair(first, second) ? [MakeBlock(first, second)] : []);

    public Task<IReadOnlyList<Block>> FindPairsAmongAsync(
        UserId userId, IReadOnlyCollection<UserId> otherUserIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Block>>(
            [.. otherUserIds.Where(other => HasPair(userId, other)).Select(other => MakeBlock(userId, other))]);

    public Task<BlockPair?> FindOutboundOwnedPairAsync(
        BlockId blockId, UserId blockerId, CancellationToken cancellationToken) =>
        Task.FromResult<BlockPair?>(null);

    public Task<Block?> FindByIdForUpdateAsync(BlockId blockId, CancellationToken cancellationToken) =>
        Task.FromResult<Block?>(null);

    public Task<IReadOnlyList<BlockedUserProjection>> ListOutboundAsync(
        UserId blockerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BlockedUserProjection>>([]);

    public void Add(Block block)
    {
    }

    public void Remove(Block block)
    {
    }

    private bool HasPair(UserId first, UserId second) =>
        Pairs.Any(p => (p.First == first && p.Second == second) || (p.First == second && p.Second == first));

    private static Block MakeBlock(UserId blocker, UserId blocked) =>
        Block.Create(blocker, blocked, Username.Create("blocked_user"), new TestClock(Instant.FromUtc(2026, 8, 16, 12, 0)));
}

internal sealed class TestClock(Instant current) : IClock
{
    public Instant GetCurrentInstant() => current;
}
