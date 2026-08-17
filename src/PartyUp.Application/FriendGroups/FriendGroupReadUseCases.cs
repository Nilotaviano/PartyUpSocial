using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Polls;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.FriendGroups;

/// <summary>
/// One roster row (D-199): visible only to the group's owner or an Accepted member.
/// <see cref="Masked"/> is the D-076 render-time posture for a member who is a blocked counterpart of
/// the VIEWER — <see cref="DisplayName"/>/<see cref="Avatar"/> are null when set, but the row itself
/// (and its <c>Status</c>) still lists: blocking never removes a retained membership row (D-202).
/// </summary>
public sealed record FriendGroupMemberView(
    UserId UserId,
    string Status,
    Instant InvitedAt,
    Instant? AcceptedAt,
    DisplayName? DisplayName,
    Avatar? Avatar,
    bool Masked)
{
    internal static FriendGroupMemberView From(FriendGroupMember member, UserNameplate? nameplate, bool masked) =>
        new(
            member.UserId,
            StatusText(member.Status),
            member.InvitedAt,
            member.AcceptedAt,
            masked ? null : nameplate?.DisplayName,
            masked ? null : nameplate?.Avatar,
            masked);

    private static string StatusText(FriendGroupMemberStatus status) => status switch
    {
        FriendGroupMemberStatus.Invited => "invited",
        FriendGroupMemberStatus.Accepted => "accepted",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown friend group member status."),
    };
}

/// <summary>A pending invite the actor holds in someone else's group — name + owner only (D-199).</summary>
public sealed record FriendGroupInviteSummary(FriendGroupId Id, string Name, UserId OwnerId, Instant InvitedAt);

public sealed record FriendGroupListResult(
    IReadOnlyList<FriendGroupSummary> Owned,
    IReadOnlyList<FriendGroupSummary> MemberOf,
    IReadOnlyList<FriendGroupInviteSummary> Invites);

/// <summary>
/// <see cref="Members"/> is null for an <c>Invited</c>-only viewer (name + owner is all they may read,
/// D-199) and populated for the owner or an Accepted member. <see cref="OwnerDisplayName"/>/
/// <see cref="OwnerAvatar"/> route through the identical D-076 masking path a roster row takes
/// (<see cref="OwnerMasked"/>) rather than special-casing the owner — D-202 makes an owner↔member
/// block impossible (it deletes the membership row outright), so <c>OwnerMasked</c> can never actually
/// be true for a viewer who reaches this detail at all, but the code does not assume that invariant.
/// </summary>
public sealed record FriendGroupDetail(
    FriendGroupId Id,
    string Name,
    UserId OwnerId,
    Instant CreatedAt,
    DisplayName? OwnerDisplayName,
    Avatar? OwnerAvatar,
    bool OwnerMasked,
    IReadOnlyList<FriendGroupMemberView>? Members);

/// <summary>
/// The FriendGroup read surface (D-199): every group the actor <see cref="FriendGroup.Involves"/>,
/// split by the actor's own relationship to it, and one group's detail — fail-closed to <c>null</c>
/// for anyone not involved, identical to a nonexistent group (D-198/D-199).
/// </summary>
public sealed class FriendGroupReadUseCases(IFriendGroupRepository groups, IBlockRepository blocks)
{
    public async Task<FriendGroupListResult> ListMineAsync(UserId actorId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<FriendGroup> involved = await groups.ListInvolvingAsync(actorId, cancellationToken);

        List<FriendGroupSummary> owned = [];
        List<FriendGroupSummary> memberOf = [];
        List<FriendGroupInviteSummary> invites = [];

        foreach (FriendGroup group in involved)
        {
            if (group.OwnerId == actorId)
            {
                owned.Add(FriendGroupSummary.From(group));
                continue;
            }

            FriendGroupMember? row = group.Members.FirstOrDefault(member => member.UserId == actorId);
            if (row is null)
            {
                continue;
            }

            if (row.Status == FriendGroupMemberStatus.Accepted)
            {
                memberOf.Add(FriendGroupSummary.From(group));
            }
            else
            {
                invites.Add(new FriendGroupInviteSummary(group.Id, group.Name.Value, group.OwnerId, row.InvitedAt));
            }
        }

        return new FriendGroupListResult(owned, memberOf, invites);
    }

    /// <summary>
    /// D-199/D-076: for an <c>Invited</c>-only viewer, name + owner is all they may read, so this
    /// resolves no identities at all — the owner id rides the wire unmasked/nameless exactly as
    /// before. For the owner or an Accepted member, resolves the owner's and every roster member's
    /// nameplate in one bulk, non-locking prefetch (mirrors <c>PollReadUseCases.GetPollDetailAsync</c>)
    /// and masks any counterpart the VIEWER is blocked with — a retained non-friend member (D-200)
    /// still gets a name; a blocked-pair member does not (D-076), while their membership row is
    /// unaffected either way.
    /// </summary>
    public async Task<FriendGroupDetail?> GetAsync(
        UserId actorId,
        FriendGroupId groupId,
        CancellationToken cancellationToken)
    {
        FriendGroup? group = await groups.FindNoTrackingAsync(groupId, cancellationToken);
        if (group is null || !group.Involves(actorId))
        {
            return null;
        }

        if (!group.HasAcceptedMember(actorId))
        {
            return new FriendGroupDetail(group.Id, group.Name.Value, group.OwnerId, group.CreatedAt, null, null, false, null);
        }

        // Every identity the response can name — the owner plus the roster, actor included: the
        // actor's own row (if they hold one) still needs their real nameplate, unlike the block query
        // below where a self-pair is meaningless and skipped.
        List<UserId> identityIds = [group.OwnerId, .. group.Members.Select(member => member.UserId)];
        IReadOnlyDictionary<Guid, UserNameplate> nameplates =
            await groups.FindNameplatesAsync(identityIds, cancellationToken);

        List<UserId> counterpartIds = [.. identityIds];
        counterpartIds.Remove(actorId);
        IReadOnlyList<Block> viewerBlocks = counterpartIds.Count == 0
            ? []
            : await blocks.FindPairsAmongAsync(actorId, counterpartIds, cancellationToken);
        HashSet<UserId> blockedCounterpartIds =
            [.. viewerBlocks.Select(block => block.BlockerId == actorId ? block.BlockedId : block.BlockerId)];

        bool ownerMasked = group.OwnerId != actorId && blockedCounterpartIds.Contains(group.OwnerId);
        UserNameplate? ownerNameplate = nameplates.GetValueOrDefault(group.OwnerId.Value);

        List<FriendGroupMemberView> members = [];
        foreach (FriendGroupMember member in group.Members)
        {
            bool masked = member.UserId != actorId && blockedCounterpartIds.Contains(member.UserId);
            UserNameplate? nameplate = nameplates.GetValueOrDefault(member.UserId.Value);
            members.Add(FriendGroupMemberView.From(member, nameplate, masked));
        }

        return new FriendGroupDetail(
            group.Id,
            group.Name.Value,
            group.OwnerId,
            group.CreatedAt,
            ownerMasked ? null : ownerNameplate?.DisplayName,
            ownerMasked ? null : ownerNameplate?.Avatar,
            ownerMasked,
            members);
    }
}
